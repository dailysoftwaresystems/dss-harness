using DssHarness.Core.Codebase.Languages.Scanning;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// One JavaScript or TypeScript file's reading over the structural scanner: its imports and exports - ECMAScript's and
/// CommonJS's - each binding an import of the contract's; what stands at its top and in its namespaces - each class,
/// interface, enum, type alias, namespace and function, a function, an arrow or a class bound to a name read as one, and
/// each variable - and in its types - each constructor, its parameter properties, method, accessor, field and enum
/// member - with its lines, signature and summary; and each call a body makes by its name and arity, with what its
/// receiver is where the file says. TypeScript's types are read where <c>typescript</c> reads, and stand nowhere in
/// <c>javascript</c>.
/// </summary>
/// <remarks>
/// <para>
/// A module - a file that imports or exports, by ECMAScript's statements or CommonJS's <c>require</c> and
/// <c>module.exports</c>, or whose extension says it is one - is an open scope named by its path, and what it does not
/// export is kept to it (<see cref="ExtractNode.SeenIn"/>), so an importer finds what it exports alone; a script's
/// declarations are the repository's, seen from everywhere, as a browser's globals are. Each name an import binds is an
/// import of the file it names: those it binds by their own names one import showing them; a default import, a renamed
/// one and a namespace one each an import by a prefix - the name it binds - showing the name the file exports it by, so a
/// reference written by that name is written as the export's, qualified by the prefix. A name another file imports a
/// declaration by besides its own - <c>default</c>, <c>export { a as b }</c>'s <c>b</c> - is among its
/// <see cref="ExtractNode.Exports"/>. A specifier is imported as Node and TypeScript try it: a path with no extension
/// with each of the language's, then the directory's <c>index</c>, each import standing in for the one before it
/// (<see cref="ExtractImport.Fallback"/>); a relative one by its path, a bare one by its address.
/// </para>
/// <para>
/// What it cannot bind is marked dynamic: a call through a computed member (<c>a[name](</c>), <c>eval</c>,
/// <c>new Function</c>, <c>Reflect.apply</c> and <c>Reflect.construct</c>, a function value's <c>call</c> and
/// <c>apply</c>, a call through a function value - a parameter, a local, a variable of no function, a field of a function
/// type - and a call on a value typed <c>any</c>. In a test file (under <c>test/</c>, <c>tests/</c> or
/// <c>__tests__/</c>, or named <c>*.test.*</c> or <c>*.spec.*</c>), each <c>describe</c>, <c>context</c> and
/// <c>suite</c> call is a test group and each <c>it</c>, <c>test</c> and <c>specify</c> call a test, with
/// <c>.only</c>, <c>.skip</c> and <c>.each</c> and the rest, named by its description as written. What a module's top
/// does outside its declarations is the calls of one node, <c>(module)</c>, made where it is first needed.
/// </para>
/// </remarks>
internal sealed class JsReading
{
    /// <summary>JavaScript's reserved words, and the few TypeScript reserves beside them.</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "break", "case", "catch", "class", "const", "continue", "debugger", "default", "delete", "do", "else", "enum", "export",
        "extends", "false", "finally", "for", "function", "if", "import", "in", "instanceof", "new", "null", "return", "super",
        "switch", "this", "throw", "true", "try", "typeof", "var", "void", "while", "with", "yield", "let", "await",
    };

    /// <summary>The names no call is made by: the reserved words, and those a parenthesis follows that call nothing.</summary>
    private static readonly HashSet<string> NoCalls = new(Reserved, StringComparer.Ordinal)
    {
        "async", "of", "as", "satisfies", "keyof", "infer", "is", "asserts", "unique", "readonly", "declare", "abstract", "implements",
        "interface", "package", "private", "protected", "public", "static", "override", "accessor",
    };

    /// <summary>The words of a type that name no type of it: what says how a type is made of others.</summary>
    private static readonly HashSet<string> TypeWords = new(StringComparer.Ordinal)
    {
        "keyof", "typeof", "infer", "extends", "readonly", "unique", "is", "asserts", "in", "out", "as", "new", "abstract", "const",
        "true", "false", "null", "undefined", "void", "this",
    };

    /// <summary>The types the language and its standard library declare, which every file sees and the repository does not: no type a reference names.</summary>
    private static readonly HashSet<string> Builtins = new(StringComparer.Ordinal)
    {
        "string", "number", "boolean", "bigint", "symbol", "object", "any", "unknown", "never", "Array", "ReadonlyArray", "Promise",
        "PromiseLike", "Record", "Partial", "Required", "Readonly", "Pick", "Omit", "Exclude", "Extract", "NonNullable", "ReturnType",
        "Parameters", "ConstructorParameters", "InstanceType", "Awaited", "ThisType", "Uppercase", "Lowercase", "Capitalize",
        "Uncapitalize", "Map", "Set", "WeakMap", "WeakSet", "ReadonlyMap", "ReadonlySet", "Date", "Error", "TypeError", "RangeError",
        "RegExp", "Function", "Object", "String", "Number", "Boolean", "Symbol", "BigInt", "Iterable", "Iterator", "IterableIterator",
        "AsyncIterable", "AsyncIterator", "Generator", "AsyncGenerator", "ArrayLike", "PropertyKey", "ArrayBuffer", "Uint8Array",
        "JSON", "Math", "Intl", "globalThis",
    };

    /// <summary>The words that may stand before a member's name, saying how it is declared.</summary>
    private static readonly HashSet<string> MemberModifiers = new(StringComparer.Ordinal)
    {
        "public", "private", "protected", "static", "readonly", "abstract", "override", "declare", "async", "accessor",
    };

    /// <summary>The words that may stand before a parameter's name in a constructor, making it a property of its type.</summary>
    private static readonly HashSet<string> ParameterModifiers = new(StringComparer.Ordinal) { "public", "private", "protected", "readonly", "override" };

    /// <summary>The calls of a test framework that declare a group of tests.</summary>
    private static readonly HashSet<string> TestGroups = new(StringComparer.Ordinal) { "describe", "context", "suite" };

    /// <summary>The calls of a test framework that declare a test.</summary>
    private static readonly HashSet<string> TestCases = new(StringComparer.Ordinal) { "it", "test", "specify" };

    /// <summary>The members of a test call that are one too: <c>it.only</c>, <c>test.each</c>, <c>test.describe</c>.</summary>
    private static readonly HashSet<string> TestVariants = new(StringComparer.Ordinal)
    {
        "only", "skip", "todo", "each", "concurrent", "failing", "fails", "fixme", "describe", "serial", "parallel",
    };

    /// <summary>The endings and parts of the names of the files bundlers and generators write.</summary>
    private static readonly string[] GeneratedEndings = [".min.js", ".min.mjs", ".min.cjs", ".bundle.js"];

    /// <summary>
    /// How TypeScript writes a type's arguments: types, unions, intersections, a qualified name's dot, a function type's
    /// arrow and a tuple's spread - groups and nested lists passed over - followed, where a call is written with them, by
    /// its parenthesis.
    /// </summary>
    private static readonly ScanTypeArguments TsTypes = new(
        new HashSet<string>(StringComparer.Ordinal) { ",", ".", "?", "|", "&", "=>", "...", "-" },
        new HashSet<string>(StringComparer.Ordinal) { "(" },
        Braces: true);

    /// <summary>How JavaScript writes its calls: a comma may end the arguments, with none after it.</summary>
    private static readonly ScanCallRules JsCalls = new(NoCalls, null, TrailingComma: true);

    /// <summary>How TypeScript writes its calls: JavaScript's, with type arguments between the name and the parenthesis.</summary>
    private static readonly ScanCallRules TsCalls = new(NoCalls, TsTypes, TrailingComma: true);

    /// <summary>The punctuators a member's name follows its receiver after: a dot and a null-aware one.</summary>
    private static readonly HashSet<string> Accessors = new(StringComparer.Ordinal) { ".", "?." };

    /// <summary>The words after which a line break does not end a statement: an expression or a declaration goes on.</summary>
    private static readonly HashSet<string> GoesOn = new(StringComparer.Ordinal)
    {
        "in", "of", "instanceof", "typeof", "new", "delete", "void", "await", "yield", "extends", "implements", "as", "satisfies",
        "keyof", "is", "asserts", "case", "export", "import", "default", "async", "else", "do", "try", "finally", "const", "let",
        "var", "function", "class", "interface", "type", "enum", "namespace", "module", "declare", "abstract", "static", "public",
        "private", "protected", "readonly", "override", "get", "set",
    };

    /// <summary>The words that, first on a line, go on with the expression before it.</summary>
    private static readonly HashSet<string> Continuing = new(StringComparer.Ordinal) { "instanceof", "in", "of", "as", "satisfies", "extends", "implements" };

    /// <summary>The extensions a specifier that names a script already has, so no other is tried.</summary>
    private static readonly HashSet<string> ScriptExtensions = new(StringComparer.OrdinalIgnoreCase) { ".js", ".jsx", ".mjs", ".cjs", ".ts", ".tsx", ".mts", ".cts", ".json", ".node" };

    /// <summary>The extensions TypeScript tries a path written with none with, in order.</summary>
    private static readonly string[] TypeScriptTries = [".ts", ".tsx", ".d.ts", ".js", ".jsx"];

    /// <summary>The extensions Node and its bundlers try a path written with none with, in order.</summary>
    private static readonly string[] NodeTries = [".js", ".jsx", ".mjs", ".cjs", ".ts", ".tsx", ".json"];

    /// <summary>The extensions either tries, TypeScript's first, each once.</summary>
    private static readonly string[] EitherTries = [.. TypeScriptTries.Union(NodeTries)];

    /// <summary>The extensions of what a module imports that is no script, imported as it is named: styles, images, markup, data.</summary>
    private static readonly HashSet<string> AssetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".css", ".scss", ".sass", ".less", ".styl", ".svg", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".avif", ".ico", ".bmp", ".html",
        ".htm", ".vue", ".svelte", ".astro", ".md", ".mdx", ".graphql", ".gql", ".wasm", ".txt", ".yaml", ".yml", ".toml", ".xml",
        ".woff", ".woff2", ".ttf", ".otf", ".eot", ".mp3", ".mp4", ".webm", ".wav", ".csv",
    };

    private readonly InterpolatedText _text;
    private readonly ScannedText _scan;
    private readonly string _prefix;
    private readonly string _path;
    private readonly bool _typescript;
    private readonly bool _markup;
    private readonly bool _declarations;
    private readonly bool _testFile;
    private readonly ScanTypeArguments? _types;
    private readonly ScanCallRules _calls;
    private readonly ScanCallRules _memberCalls;
    private readonly ScanExtract _extract = new();

    /// <summary>Each name an import binds, by that name.</summary>
    private readonly Dictionary<string, Binding> _bindings = new(StringComparer.Ordinal);

    /// <summary>The keys of the declarations at the file's top, by their names: what an export list names.</summary>
    private readonly Dictionary<string, List<string>> _topKeys = new(StringComparer.Ordinal);

    /// <summary>What the file exports by a list or an assignment rather than where it declares it: each local name, the name it is exported by, and the line.</summary>
    private readonly List<(string Local, string Exported, int Line)> _exported = [];

    /// <summary>The names of the type parameters in force: no type the repository declares.</summary>
    private readonly HashSet<string> _typeParameters = new(StringComparer.Ordinal);

    /// <summary>The fields of a function type each type the file declares holds, by the type's name.</summary>
    private readonly Dictionary<string, HashSet<string>> _functionFields = new(StringComparer.Ordinal);

    /// <summary>The variables at the file's top no function or class is bound to: a call of one is through its value.</summary>
    private readonly HashSet<string> _values = new(StringComparer.Ordinal);

    /// <summary>The specifiers a <c>require</c> or an <c>import(...)</c> imports already, each imported once.</summary>
    private readonly HashSet<string> _required = new(StringComparer.Ordinal);

    /// <summary>The bodies read once the declarations around them are: the file's, then each type's members'.</summary>
    private List<Action>? _bodies;

    /// <summary>Whether the file is a module, whose declarations are its own but what it exports.</summary>
    private bool _module;

    /// <summary>The scope the file's top-level declarations are put in: its path in a module, none in a script.</summary>
    private string? _scope;

    /// <summary>The key of the node what the file's top does outside its declarations is the calls of, once made.</summary>
    private string? _moduleKey;

    /// <summary>Reads <paramref name="text"/>, the file <paramref name="path"/>.</summary>
    /// <param name="language">The implementation's name, which prefixes every id.</param>
    /// <param name="path">The file's path.</param>
    /// <param name="text">Its text.</param>
    /// <param name="typescript">Whether it is TypeScript, whose types the reader takes.</param>
    public JsReading(string language, string path, string text, bool typescript)
    {
        var name = CodeGraphIds.FileName(path);

        _typescript = typescript;

        // JavaScript writes no type that a < could open, so every file of it may hold markup; TypeScript's .tsx alone does.
        _markup = !typescript || name.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase);
        _text = JsLexer.Scan(text, _markup);
        _scan = _text.Scan;
        _prefix = $"{language}:";
        _path = path;
        _declarations = typescript && (name.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".d.mts", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".d.cts", StringComparison.OrdinalIgnoreCase));
        _testFile = IsTestPath(path);
        _types = typescript ? TsTypes : null;
        _calls = typescript ? TsCalls : JsCalls;
        _memberCalls = _calls with { Keywords = new HashSet<string>(StringComparer.Ordinal) };
    }

    /// <summary>The file's extract.</summary>
    public FileExtract Read()
    {
        _module = IsModule();
        _scope = _module ? _path : null;

        if (_module)
        {
            _extract.Scope(_path, open: true, parent: null);
        }

        Directives();

        var top = new ScanFrame(_scope, _scope, null, null);
        _bodies = [];

        try
        {
            ScanFrame.Walk(0, _scan.Count, at => Statement(at, _scan.Count, top, _module ? _path : null));

            // Read once every declaration of the file is: a body sees what the file declares after it.
            for (var at = 0; at < _bodies.Count; at++)
            {
                _bodies[at]();
            }
        }
        finally
        {
            _bodies = null;
        }

        Exported();
        _extract.Generated = Generated();
        return _extract.Build();
    }

    /// <summary>Whether <paramref name="path"/> is a test file's: under a directory of tests, or named as a test is.</summary>
    private static bool IsTestPath(string path)
    {
        var name = CodeGraphIds.FileName(path);

        return name.Contains(".test.", StringComparison.OrdinalIgnoreCase) || name.Contains(".spec.", StringComparison.OrdinalIgnoreCase)
            || path.Split('/')[..^1].Any(directory => directory is "test" or "tests" or "__tests__");
    }

    /// <summary>Why the file is generated: its name a bundler's or a generator's, or what its first lines say; <see langword="null"/> where it is not.</summary>
    private string? Generated()
    {
        var name = CodeGraphIds.FileName(_path);

        return GeneratedEndings.FirstOrDefault(ending => name.EndsWith(ending, StringComparison.OrdinalIgnoreCase)) is { } ending
            ? $"its name ends {ending}, as a bundler names what it writes"
            : name.Contains(".generated.", StringComparison.OrdinalIgnoreCase)
                ? "its name holds .generated., as a generator names what it writes"
                : ScanComments.Generated(_scan);
    }

    /// <summary>
    /// Whether the file is a module: its extension says so (<c>.mjs</c>, <c>.cjs</c>, <c>.mts</c>, <c>.cts</c>), an
    /// <c>import</c> or <c>export</c> statement stands at its top, or it calls <c>require</c> on a specifier or assigns
    /// <c>module.exports</c> or <c>exports</c> anywhere.
    /// </summary>
    private bool IsModule()
    {
        var name = CodeGraphIds.FileName(_path);

        if (name.LastIndexOf('.') is var dot and > 0 && name[dot..].ToLowerInvariant() is ".mjs" or ".cjs" or ".mts" or ".cts")
        {
            return true;
        }

        for (var at = 0; at < _scan.Count; at = _scan.Opens(at) ? _scan.Past(at) : at + 1)
        {
            if (IsStatement(at, "import") || IsStatement(at, "export"))
            {
                return true;
            }
        }

        for (var at = 0; at < _scan.Count; at++)
        {
            if (!_scan.IsName(at) || IsMember(_scan, _scan.Previous(at)))
            {
                continue;
            }

            var next = _scan.Next(at);

            if ((_scan.Is(at, "require") && _scan.Is(next, "(") && IsText(_scan.Next(next)))
                || (_scan.Is(at, "module") && _scan.Is(next, ".") && _scan.Is(_scan.Next(next), "exports"))
                || (_scan.Is(at, "exports") && (_scan.Is(next, ".") || _scan.Is(next, "["))))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether an <c>import</c> or <c>export</c> statement - <paramref name="word"/> - begins at <paramref name="at"/>: neither a call of <c>import(...)</c>, nor <c>import.meta</c>, nor a key or a member so named.</summary>
    private bool IsStatement(int at, string word)
    {
        if (!_scan.Is(at, word) || IsMember(_scan, _scan.Previous(at)))
        {
            return false;
        }

        var next = _scan.Next(at);
        return next < _scan.Count && !_scan.Is(next, "(") && !_scan.Is(next, ".") && !_scan.Is(next, ":") && !_scan.Is(next, "?.");
    }

    // ---- Imports, and exports from other files ----

    /// <summary>A name an import binds: the specifier of the file it is imported from, the name that file exports it by - none for the file's namespace - and whether it is reached by a prefix of its own.</summary>
    private sealed record Binding(string Specifier, string? Export, bool Prefixed);

    /// <summary>
    /// Each import at the file's top: an <c>import</c> statement, TypeScript's <c>import x = require(...)</c>, an
    /// <c>export ... from</c>, and a <c>const</c>, <c>let</c> or <c>var</c> given a <c>require</c> of a specifier.
    /// </summary>
    private void Directives()
    {
        for (var at = 0; at < _scan.Count; at = _scan.Opens(at) ? _scan.Past(at) : at + 1)
        {
            if (IsStatement(at, "import"))
            {
                Import(at);
            }
            else if (IsStatement(at, "export"))
            {
                ExportFrom(at);
            }
            else if ((_scan.Is(at, "const") || _scan.Is(at, "let") || _scan.Is(at, "var")) && !IsMember(_scan, _scan.Previous(at)))
            {
                Required(at);
            }
        }
    }

    /// <summary>An <c>import</c> statement at <paramref name="at"/>: its bindings, each an import of the file it names.</summary>
    private void Import(int at)
    {
        var line = _scan[at].Line;
        var stop = StatementEnd(at, _scan.Count);
        var next = _scan.Next(at);

        // import './side-effect': an import that makes no name visible.
        if (IsText(next))
        {
            if (Specifier(next) is { } bare)
            {
                Run(bare, line, null, [], export: false);
            }

            return;
        }

        // import type ...: the word is the clause's where a binding follows it, and a default import's name otherwise.
        if (_typescript && _scan.Is(next, "type") && _scan.Next(next) is var after
            && ((_scan.IsName(after) && !_scan.Is(after, "from")) || _scan.Is(after, "{") || _scan.Is(after, "*")))
        {
            next = after;
        }

        // TypeScript's import x = require('y') and import x = A.B.
        if (_scan.IsName(next) && _scan.Is(_scan.Next(next), "="))
        {
            var value = _scan.Next(_scan.Next(next));

            if (RequireAt(value) is { } required)
            {
                Bind(_scan.Text(next), required.Specifier, null, line);
            }
            else if (Chain(_scan, value, stop).Chain is { } aliased)
            {
                _extract.Import(new ExtractImport(CodeImportForms.Scope, aliased, line) { Prefix = _scan.Text(next) });
            }

            return;
        }

        var from = -1;

        for (var index = next; index < stop; index = _scan.Opens(index) ? _scan.Past(index) : index + 1)
        {
            if (_scan.Is(index, "from") && IsText(_scan.Next(index)))
            {
                from = index;
                break;
            }
        }

        if (from < 0 || Specifier(_scan.Next(from)) is not { } specifier)
        {
            return;
        }

        var named = new List<string>();

        for (var clause = next; clause < from;)
        {
            if (_scan.Is(clause, ","))
            {
                clause = _scan.Next(clause);
            }
            else if (_scan.Is(clause, "*") && _scan.Is(_scan.Next(clause), "as") && _scan.IsName(_scan.Next(_scan.Next(clause))))
            {
                var local = _scan.Next(_scan.Next(clause));
                Bind(_scan.Text(local), specifier, null, line);
                clause = _scan.Next(local);
            }
            else if (_scan.Is(clause, "{"))
            {
                var (inner, past) = _scan.Bounds(clause, from);

                foreach (var (pieceStart, pieceEnd) in ScanCalls.Split(_scan, clause + 1, inner, null))
                {
                    if (Specified(pieceStart, pieceEnd) is not var (imported, local))
                    {
                        continue;
                    }

                    if (imported == local && imported != "default")
                    {
                        named.Add(imported);
                        _bindings[local] = new Binding(specifier, imported, Prefixed: false);
                    }
                    else
                    {
                        Bind(local, specifier, imported, line);
                    }
                }

                clause = past;
            }
            else if (_scan.IsName(clause))
            {
                Bind(_scan.Text(clause), specifier, "default", line);
                clause = _scan.Next(clause);
            }
            else
            {
                break;
            }
        }

        if (named.Count > 0)
        {
            Run(specifier, line, null, named, export: false);
        }
    }

    /// <summary>
    /// One specifier of an import's or an export's braces from <paramref name="start"/> to <paramref name="end"/> - past a
    /// <c>type</c> - the name the other file has and the name this one gives it; <see langword="null"/> where it is none.
    /// </summary>
    private (string Imported, string Local)? Specified(int start, int end)
    {
        var at = start;

        if (_scan.Is(at, "type") && _scan.Next(at) < end && !_scan.Is(_scan.Next(at), "as"))
        {
            at = _scan.Next(at);
        }

        var imported = _scan.IsName(at) ? _scan.Text(at) : IsText(at) ? Unquoted(_scan.Text(at)) : null;

        if (imported is not { Length: > 0 })
        {
            return null;
        }

        var next = _scan.Next(at);
        var local = _scan.Is(next, "as") && _scan.Next(next) < end && _scan.IsName(_scan.Next(next)) ? _scan.Text(_scan.Next(next)) : imported;
        return (imported, local);
    }

    /// <summary>
    /// A binding of <paramref name="local"/> to what the file <paramref name="specifier"/> names exports by
    /// <paramref name="export"/> - its namespace where that is none - reached by a prefix of its own: an import by it.
    /// </summary>
    private void Bind(string local, string specifier, string? export, int line)
    {
        _bindings[local] = new Binding(specifier, export, Prefixed: true);
        Run(specifier, line, local, export is null ? null : [export], export: false);
    }

    /// <summary>
    /// An <c>export ... from</c> at <paramref name="at"/>: what the file it names exports, made visible to every file that
    /// imports this one - all of it, or the names it lists as that file declares them. A namespace it gives a name
    /// (<c>export * as ns from</c>) is no name this one exports.
    /// </summary>
    private void ExportFrom(int at)
    {
        var line = _scan[at].Line;
        var stop = StatementEnd(at, _scan.Count);
        var next = _scan.Next(at);

        if (_typescript && _scan.Is(next, "type") && (_scan.Is(_scan.Next(next), "{") || _scan.Is(_scan.Next(next), "*")))
        {
            next = _scan.Next(next);
        }

        var star = _scan.Is(next, "*");
        var braces = _scan.Is(next, "{");

        if (!star && !braces)
        {
            return;
        }

        var after = star ? _scan.Next(next) : _scan.Past(next);

        // export * as ns from './x': the file is imported, its namespace no name of this one's importers.
        if (star && _scan.Is(after, "as"))
        {
            after = _scan.Next(_scan.Next(after));

            if (_scan.Is(after, "from") && after < stop && Specifier(_scan.Next(after)) is { } named)
            {
                Run(named, line, null, [], export: false);
            }

            return;
        }

        if (!_scan.Is(after, "from") || after >= stop || Specifier(_scan.Next(after)) is not { } specifier)
        {
            return;
        }

        // export * re-exports every name but the module's default.
        if (star)
        {
            Run(specifier, line, null, null, export: true, hide: ["default"]);
            return;
        }

        var shown = ScanCalls.Split(_scan, next + 1, _scan.Bounds(next, after).Inner, null)
            .Select(piece => Specified(piece.Start, piece.End))
            .OfType<(string Imported, string Local)>()
            .Select(specified => specified.Imported)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (shown.Count > 0)
        {
            Run(specifier, line, null, shown, export: true);
        }
    }

    /// <summary>
    /// The declarators after a <c>const</c>, <c>let</c> or <c>var</c> at <paramref name="at"/>'s, each that a
    /// <c>require</c> declares (<see cref="RequireDeclared"/>) bound: a name to the file's namespace, or to the export a
    /// member of it names (<c>require('./x').default</c>); an object pattern each name it takes, by itself or renamed - one
    /// that takes none (<c>{ ...rest }</c>, <c>{}</c>) the file imported, no name of it bound.
    /// </summary>
    private void Required(int at)
    {
        var stop = StatementEnd(at, _scan.Count);
        var boundary = _scan.Is(LastOf(stop), ";") ? LastOf(stop) : stop;

        foreach (var (start, end) in Declarators(_scan.Next(at), boundary))
        {
            if (RequireDeclared(start, end) is not { } required)
            {
                continue;
            }

            var line = _scan[start].Line;

            if (_scan.IsName(start))
            {
                Bind(_scan.Text(start), required.Specifier, required.Member, line);
                continue;
            }

            var named = new List<string>();
            var bound = false;

            foreach (var (pieceStart, pieceEnd) in ScanCalls.Split(_scan, start + 1, _scan.Match(start), null))
            {
                if (!_scan.IsName(pieceStart))
                {
                    continue;
                }

                var imported = _scan.Text(pieceStart);
                var colon = _scan.Next(pieceStart);
                var local = _scan.Is(colon, ":") && _scan.Next(colon) < pieceEnd && _scan.IsName(_scan.Next(colon)) ? _scan.Text(_scan.Next(colon)) : imported;

                if (local == imported)
                {
                    named.Add(imported);
                    _bindings[local] = new Binding(required.Specifier, imported, Prefixed: false);
                }
                else
                {
                    Bind(local, required.Specifier, imported, line);
                    bound = true;
                }
            }

            if (named.Count > 0 || !bound)
            {
                Run(required.Specifier, line, null, named, export: false);
            }
        }
    }

    /// <summary>
    /// What the declarator from <paramref name="start"/> to <paramref name="end"/> declares a <c>require</c> of, where its
    /// value is one alone (<see cref="RequiredValue"/>): a name - its type written or not - the file's namespace or a member
    /// of it; an object pattern the names it takes, of the namespace alone; <see langword="null"/> where it declares none,
    /// its value code whose calls are read - <c>require('./routes')(app)</c>, <c>{ a } = require('./x').b</c>.
    /// </summary>
    private (string Specifier, string? Member)? RequireDeclared(int start, int end)
    {
        var pattern = _scan.Is(start, "{") && _scan.Match(start) is var close and > 0 && close < end;

        if (!pattern && !_scan.IsName(start))
        {
            return null;
        }

        var cursor = _scan.Next(pattern ? _scan.Match(start) : start);

        if (_scan.Is(cursor, "!"))
        {
            cursor = _scan.Next(cursor);
        }

        var assign = _typescript && _scan.Is(cursor, ":") ? ScanCalls.TopLevel(_scan, _scan.Next(cursor), end, "=", _types) : _scan.Is(cursor, "=") ? cursor : end;

        return assign < end && RequiredValue(_scan.Next(assign)) is { } required && required.Past >= end && !(pattern && required.Member is not null)
            ? (required.Specifier, required.Member)
            : null;
    }

    /// <summary>
    /// The <c>require</c> a value at <paramref name="at"/> is the whole of - <c>require('x')</c>, or a member of what it
    /// answers, <c>require('x').name</c> - its specifier, the member's name, and the index past it; <see langword="null"/>
    /// where the value is more, or none.
    /// </summary>
    private (string Specifier, string? Member, int Past)? RequiredValue(int at)
    {
        if (RequireAt(at) is not { } required)
        {
            return null;
        }

        if (Alone(required.Past))
        {
            return (required.Specifier, null, required.Past);
        }

        var member = _scan.Next(required.Past);
        return _scan.Is(required.Past, ".") && _scan.IsName(member) && Alone(_scan.Next(member)) ? (required.Specifier, _scan.Text(member), _scan.Next(member)) : null;
    }

    /// <summary>Whether a declaration's initializer ends at <paramref name="past"/>: the statement, or the declarator, ends there.</summary>
    private bool Alone(int past) => past >= _scan.Count || _scan.Is(past, ";") || _scan.Is(past, ",") || _scan.Is(past, "}") || _scan[past].Line > _scan[past - 1].EndLine;

    /// <summary>
    /// The specifier a <c>require</c> at <paramref name="at"/> is given, and the index past its call - <c>require('x')</c>,
    /// or TypeScript's <c>require</c> after <c>import x =</c>; <see langword="null"/> where none is there.
    /// </summary>
    private (string Specifier, int Past)? RequireAt(int at)
    {
        var open = _scan.Next(at);

        if (!_scan.Is(at, "require") || !_scan.Is(open, "(") || !IsText(_scan.Next(open)) || _scan.Match(open) is not (var close and > 0))
        {
            return null;
        }

        return Specifier(_scan.Next(open)) is { } specifier && _scan.Next(_scan.Next(open)) == close ? (specifier, _scan.Next(close)) : null;
    }

    /// <summary>
    /// The run of imports a specifier is imported as - by a path relative to the file where it begins <c>./</c> or
    /// <c>../</c>, by its address otherwise - each standing in for the one before it as Node and TypeScript try it: the
    /// specifier as written, then with each of the language's extensions, then the directory's <c>index</c> with each; a
    /// TypeScript file's <c>.js</c> its source's <c>.ts</c> first. An absolute path or a URL imports nothing of the tree.
    /// </summary>
    private void Run(string specifier, int line, string? prefix, IReadOnlyList<string>? show, bool export, IReadOnlyList<string>? hide = null)
    {
        var relative = specifier is "." or ".." || specifier.StartsWith("./", StringComparison.Ordinal) || specifier.StartsWith("../", StringComparison.Ordinal);

        if (!relative && (specifier.StartsWith('/') || specifier.Contains("://", StringComparison.Ordinal) || specifier.StartsWith("data:", StringComparison.Ordinal)))
        {
            return;
        }

        var form = relative ? CodeImportForms.Relative : CodeImportForms.Address;
        var first = true;

        foreach (var target in Tried(specifier, typescript: _typescript, node: !_typescript))
        {
            _extract.Import(new ExtractImport(form, target, line) { Prefix = prefix, Show = show, Hide = hide, Export = export, Fallback = !first });
            first = false;
        }
    }

    /// <summary>
    /// The paths <paramref name="specifier"/> is tried as, in order, as TypeScript tries a TypeScript file's where
    /// <paramref name="typescript"/> and Node a JavaScript file's where <paramref name="node"/> (<see cref="Run"/>) - as either
    /// may, TypeScript's first and each once, where both (a package's entry, a path mapping's file): as written, then with
    /// each extension, then as a directory's <c>index</c> with each - a script's extension written, as written alone,
    /// TypeScript's source of it first; an asset's as written.
    /// </summary>
    internal static IEnumerable<string> Tried(string specifier, bool typescript, bool node)
    {
        var name = specifier[(specifier.LastIndexOf('/') + 1)..];
        var dot = name.LastIndexOf('.');
        var extension = dot > 0 ? name[dot..] : string.Empty;
        var directory = specifier is "." or ".." || specifier.EndsWith('/');

        if (!directory && ScriptExtensions.Contains(extension))
        {
            var stem = specifier[..^extension.Length];

            // TypeScript names the file it compiles to: its source is tried first.
            if (typescript)
            {
                foreach (var source in extension.ToLowerInvariant() switch
                {
                    ".js" => [".ts", ".tsx", ".d.ts"],
                    ".jsx" => [".tsx"],
                    ".mjs" => [".mts", ".d.mts"],
                    ".cjs" => [".cts", ".d.cts"],
                    _ => (string[])[],
                })
                {
                    yield return stem + source;
                }
            }

            yield return specifier;
            yield break;
        }

        if (!directory && AssetExtensions.Contains(extension))
        {
            yield return specifier;
            yield break;
        }

        var extensions = typescript && node ? EitherTries : typescript ? TypeScriptTries : NodeTries;
        var stemmed = directory ? specifier.TrimEnd('/') : specifier;

        if (!directory)
        {
            yield return specifier;

            foreach (var added in extensions)
            {
                yield return specifier + added;
            }
        }

        foreach (var added in extensions)
        {
            yield return $"{stemmed}/index{added}";
        }
    }

    /// <summary>The specifier the text at <paramref name="at"/> holds; <see langword="null"/> where it is none - empty, interpolated, or left open.</summary>
    private string? Specifier(int at) => SpecifierOf(_scan, at);

    /// <summary><paramref name="text"/>, a string, without its quotes; <see langword="null"/> where it is left open or holds an escape.</summary>
    private static string? Unquoted(string text)
        => text.Length >= 2 && text[0] is '\'' or '"' or '`' && text[^1] == text[0] && !text.Contains('\\', StringComparison.Ordinal) ? text[1..^1] : null;

    // ---- Statements: what stands at a module's top and in a namespace ----

    /// <summary>
    /// The statement that begins at <paramref name="at"/>, read in <paramref name="frame"/>: a declaration, an export, or
    /// code - what a declaration not exported is seen in being <paramref name="hidden"/>, none where every declaration of
    /// the frame is seen from everywhere. Answers where the next begins.
    /// </summary>
    private int Statement(int at, int end, ScanFrame frame, string? hidden)
    {
        var start = at;
        var marks = new List<string>();
        var decorators = new List<(int Start, int End)>();
        at = Decorators(at, end, marks, decorators);
        var head = at;

        if (at >= end)
        {
            return end;
        }

        if (_scan.Is(at, ";"))
        {
            return at + 1;
        }

        if (IsStatement(at, "import"))
        {
            return StatementEnd(at, end);
        }

        var modifiers = new List<string>();
        var exported = false;
        var isDefault = false;

        if (IsStatement(at, "export"))
        {
            var next = _scan.Next(at);
            var listed = _typescript && _scan.Is(next, "type") && _scan.Is(_scan.Next(next), "{") ? _scan.Next(next) : next;

            // export ... from, read with the imports; export { a, b as c }; TypeScript's export = and export as namespace.
            if (_scan.Is(listed, "*") || (_scan.Is(listed, "{") && _scan.Is(_scan.Past(listed), "from")) || _scan.Is(listed, "as") || _scan.Is(listed, "import"))
            {
                return StatementEnd(at, end);
            }

            if (_scan.Is(listed, "{"))
            {
                ExportList(listed, end);
                return StatementEnd(at, end);
            }

            if (_scan.Is(listed, "="))
            {
                var assigned = _scan.Next(listed);

                if (_scan.IsName(assigned) && IsEnded(_scan.Next(assigned), end))
                {
                    _exported.Add((_scan.Text(assigned), "default", _scan[assigned].Line));
                }

                return StatementEnd(at, end);
            }

            exported = true;
            modifiers.Add("export");
            at = next;

            if (_scan.Is(at, "default"))
            {
                isDefault = true;
                modifiers.Add("default");
                at = _scan.Next(at);

                // export default name;
                if (_scan.IsName(at) && !IsDeclarationWord(at) && IsEnded(_scan.Next(at), end))
                {
                    _exported.Add((_scan.Text(at), "default", _scan[at].Line));
                    return StatementEnd(at, end);
                }
            }

            at = Decorators(at, end, marks, decorators);
        }

        // A default export is exported by the name default alone: its own name is the module's, as an alias's is.
        var seenIn = exported && !isDefault ? null : hidden;

        while (true)
        {
            if (_typescript && _scan.Is(at, "declare") && _scan.IsName(_scan.Next(at)))
            {
                modifiers.Add("declare");
            }
            else if (_typescript && _scan.Is(at, "abstract") && _scan.Is(_scan.Next(at), "class"))
            {
                modifiers.Add("abstract");
            }
            else if (_scan.Is(at, "async") && _scan.Is(_scan.Next(at), "function"))
            {
                modifiers.Add("async");
            }
            else
            {
                break;
            }

            at = _scan.Next(at);
        }

        var ambient = _declarations || modifiers.Contains("declare", StringComparer.Ordinal);

        if (_scan.Is(at, "class"))
        {
            return Class(start, head, at, end, frame, modifiers, marks, decorators, seenIn, isDefault ? "default" : null, nameIsDefault: isDefault, ambient);
        }

        if (_typescript && _scan.Is(at, "interface") && _scan.IsName(_scan.Next(at)))
        {
            if (isDefault)
            {
                _exported.Add((_scan.Text(_scan.Next(at)), "default", _scan[at].Line));
            }

            return Interface(start, head, _scan.Next(at), end, frame, modifiers, marks, seenIn);
        }

        if (_typescript && (_scan.Is(at, "enum") || (_scan.Is(at, "const") && _scan.Is(_scan.Next(at), "enum"))))
        {
            if (_scan.Is(at, "const"))
            {
                modifiers.Add("const");
                at = _scan.Next(at);
            }

            if (_scan.IsName(_scan.Next(at)))
            {
                return Enum(start, head, _scan.Next(at), end, frame, modifiers, marks, seenIn);
            }
        }

        if (_typescript && _scan.Is(at, "type") && _scan.IsName(_scan.Next(at)) && (_scan.Is(_scan.Next(_scan.Next(at)), "=") || _scan.Is(_scan.Next(_scan.Next(at)), "<")))
        {
            return TypeAlias(start, head, _scan.Next(at), end, frame, modifiers, marks, seenIn);
        }

        if (_typescript && (_scan.Is(at, "namespace") || _scan.Is(at, "module")) && (_scan.IsName(_scan.Next(at)) || IsText(_scan.Next(at))))
        {
            // An ambient module, named by a string, is every file's: what it declares is a module another file imports.
            return Namespace(start, head, _scan.Next(at), end, frame, modifiers, marks, IsText(_scan.Next(at)) ? null : seenIn, ambient);
        }

        if (_typescript && ambient && _scan.Is(at, "global") && _scan.Is(_scan.Next(at), "{"))
        {
            return Global(_scan.Next(at), end, frame);
        }

        if (_scan.Is(at, "function"))
        {
            return Function(start, head, at, end, frame, modifiers, marks, decorators, seenIn, isDefault, ambient);
        }

        if ((_scan.Is(at, "const") || _scan.Is(at, "let") || _scan.Is(at, "var")) && (_scan.IsName(_scan.Next(at)) || _scan.Opens(_scan.Next(at))))
        {
            return Variables(start, head, at, end, frame, modifiers, marks, decorators, seenIn, ambient);
        }

        if (isDefault)
        {
            return DefaultExport(start, head, at, end, frame, marks, decorators);
        }

        if (CommonJs(start, at, end, frame) is var common and > 0)
        {
            return common;
        }

        // Code: what it calls is the module's - but the CommonJS exports its blocks hold, read as the top's are.
        var stop = StatementEnd(at, end);

        foreach (var (codeFrom, codeTo) in NestedCommonJs(at, stop, frame))
        {
            Defer(() => Calls(_text, codeFrom, codeTo, null, new Locals(), null, depth: 0));
        }

        return stop;
    }

    /// <summary>
    /// The CommonJS exports a statement of code at a module's top, from <paramref name="from"/> to <paramref name="to"/>,
    /// holds where a statement begins in its blocks - <c>if (typeof module !== 'undefined') { module.exports = ... }</c> -
    /// each read as one at the top is (<see cref="CommonJs"/>); answers the spans of the rest, whose calls are the module's.
    /// </summary>
    private List<(int From, int To)> NestedCommonJs(int from, int to, ScanFrame frame)
    {
        var rest = new List<(int From, int To)>();
        var begins = from;

        for (var at = from; at < to && _module && frame.Parent is null; at++)
        {
            if (at == from || !(_scan.Is(at, "module") || _scan.Is(at, "exports")) || IsMember(_scan, _scan.Previous(at))
                || !(_scan.Is(_scan.Previous(at), "{") || _scan.Is(_scan.Previous(at), ";") || _scan.Is(_scan.Previous(at), "}") || _scan.Is(_scan.Previous(at), ")")))
            {
                continue;
            }

            if (CommonJs(at, at, to, frame) is var past and > 0)
            {
                rest.Add((begins, at));
                begins = past;
                at = past - 1;
            }
        }

        rest.Add((begins, to));
        return [.. rest.Where(span => span.To > span.From)];
    }

    /// <summary>Whether the token at <paramref name="at"/> begins a declaration after <c>export default</c>: <c>class</c>, <c>function</c>, <c>async function</c>, <c>abstract class</c>, <c>interface</c>.</summary>
    private bool IsDeclarationWord(int at)
        => _scan.Is(at, "class") || _scan.Is(at, "function") || _scan.Is(at, "interface")
            || (_scan.Is(at, "async") && _scan.Is(_scan.Next(at), "function")) || (_scan.Is(at, "abstract") && _scan.Is(_scan.Next(at), "class"));

    /// <summary>Whether a statement ends at <paramref name="at"/>: a <c>;</c>, a closing brace, the text's end, or a line break before it.</summary>
    private bool IsEnded(int at, int end)
        => at >= end || at >= _scan.Count || _scan.Is(at, ";") || _scan.Is(at, "}") || _scan[at].Line > _scan[_scan.Previous(at)].EndLine;

    /// <summary>An export list - <c>{ a, b as c }</c> - at <paramref name="open"/>: each name exported by the name it is given, once the file is read.</summary>
    private void ExportList(int open, int end)
    {
        var inner = _scan.Bounds(open, end).Inner;

        foreach (var (pieceStart, pieceEnd) in ScanCalls.Split(_scan, open + 1, inner, null))
        {
            if (Specified(pieceStart, pieceEnd) is var (local, exported))
            {
                _exported.Add((local, exported, _scan[pieceStart].Line));
            }
        }
    }

    /// <summary>
    /// What an <c>export default</c> at <paramref name="at"/> exports that is no declaration: a function or an arrow, a
    /// function named <c>default</c>; any other value, a variable so named, what it calls its own.
    /// </summary>
    private int DefaultExport(int start, int head, int at, int end, ScanFrame frame, List<string> marks, List<(int Start, int End)> decorators)
    {
        var stop = StatementEnd(at, end);
        var boundary = _scan.Is(LastOf(stop), ";") ? LastOf(stop) : stop;

        if (FunctionAt(at, boundary) is { } function)
        {
            Bound(start, head, at, "default", function, frame, marks, decorators, ["export", "default"], "function", seenIn: null, exports: null);
            return stop;
        }

        var key = _extract.Add(new ExtractNode(Id(frame.Qualify("default")), CodeNodeKinds.Member, "default", frame.Qualify("default"), _scan[start].Line, _scan[LastOf(stop)].EndLine, Signature(head, at))
        {
            SubKind = "variable",
            Scope = frame.Scope,
            Parent = frame.Parent,
            Summary = ScanComments.Summary(_scan, start),
            Modifiers = ["export", "default", .. marks],
        });

        Registered(key, frame, "default");
        Defer(() => Calls(_text, at, boundary, key, new Locals(), null, depth: 0));
        return stop;
    }

    /// <summary>
    /// A CommonJS export at <paramref name="at"/> - <c>module.exports = ...</c>, <c>module.exports.x = ...</c>,
    /// <c>exports.x = ...</c> - what it assigns exported by its name, <c>default</c> for the whole of it; answers where the
    /// next statement begins, or 0 where none is there.
    /// </summary>
    private int CommonJs(int start, int at, int end, ScanFrame frame)
    {
        if (frame.Parent is not null || !_module)
        {
            return 0;
        }

        var next = _scan.Next(at);
        int target;

        if (_scan.Is(at, "module") && _scan.Is(next, ".") && _scan.Is(_scan.Next(next), "exports"))
        {
            target = _scan.Next(_scan.Next(next));
        }
        else if (_scan.Is(at, "exports") && _scan.Is(next, "."))
        {
            target = next;
        }
        else
        {
            return 0;
        }

        string exported;
        int assign;

        if (_scan.Is(target, "="))
        {
            (exported, assign) = ("default", target);
        }
        else if (_scan.Is(target, ".") && _scan.IsName(_scan.Next(target)) && _scan.Is(_scan.Next(_scan.Next(target)), "="))
        {
            (exported, assign) = (_scan.Text(_scan.Next(target)), _scan.Next(_scan.Next(target)));
        }
        else
        {
            return 0;
        }

        var stop = StatementEnd(at, end);
        var boundary = _scan.Is(LastOf(stop), ";") ? LastOf(stop) : stop;
        var value = _scan.Next(assign);
        var line = _scan[at].Line;

        if (_scan.IsName(value) && _scan.Next(value) >= boundary && !Reserved.Contains(_scan.Text(value)))
        {
            _exported.Add((_scan.Text(value), exported, line));
        }
        else if (RequireAt(value) is { } required && required.Past >= boundary)
        {
            // module.exports = require('./x') is ./x's every name; exports.k = require('./x') its namespace alone, by k.
            Run(required.Specifier, line, null, exported == "default" ? null : [], export: exported == "default");
            _required.Add(required.Specifier);
        }
        else if (exported == "default" && _scan.Is(value, "{"))
        {
            ExportedObject(value, boundary, frame);
        }
        else if (_scan.Is(value, "class"))
        {
            var name = _scan.IsName(_scan.Next(value)) && !_scan.Is(_scan.Next(value), "extends") ? _scan.Text(_scan.Next(value)) : exported;
            Class(start, start, value, boundary, frame, [], [], [], seenIn: name == exported ? null : _scope, name, nameIsDefault: false, ambient: false);

            if (name != exported)
            {
                _exported.Add((name, exported, line));
            }
        }
        else if (FunctionAt(value, boundary) is { } function)
        {
            var name = function.Name >= 0 && exported == "default" ? _scan.Text(function.Name) : exported;
            Bound(start, start, value, name, function, frame, [], [], [], "function", seenIn: name == exported ? null : _scope, exports: name == exported ? null : [exported]);
        }
        else
        {
            var key = _extract.Add(new ExtractNode(Id(frame.Qualify(exported)), CodeNodeKinds.Member, exported, frame.Qualify(exported), _scan[start].Line, _scan[LastOf(stop)].EndLine, Signature(start, assign))
            {
                SubKind = "variable",
                Scope = frame.Scope,
                Summary = ScanComments.Summary(_scan, start),
            });

            Registered(key, frame, exported);
            Defer(() => Calls(_text, value, boundary, key, new Locals(), null, depth: 0));
        }

        return stop;
    }

    /// <summary>
    /// The object literal <c>module.exports</c> is given at <paramref name="open"/>: each name it takes by itself
    /// (<c>{ a }</c>) or as another's (<c>{ b: c }</c>) exported by its key, each function it holds a function of its key.
    /// </summary>
    private void ExportedObject(int open, int end, ScanFrame frame)
    {
        var inner = Math.Min(_scan.Bounds(open, end).Inner, end);

        foreach (var (pieceStart, pieceEnd) in ScanCalls.Split(_scan, open + 1, inner, null))
        {
            var key = _scan.IsName(pieceStart) ? _scan.Text(pieceStart) : null;
            var next = _scan.Next(pieceStart);
            var line = _scan[pieceStart].Line;

            // ...require('./b'): every name the file exports, as an export * is.
            if (_scan.Is(pieceStart, "...") && RequireAt(next) is { } spread && spread.Past >= pieceEnd)
            {
                Run(spread.Specifier, line, null, null, export: true, hide: ["default"]);
                continue;
            }

            // key: require('./a') - the file is imported, its namespace no name of this one's importers.
            if (key is not null && _scan.Is(next, ":") && RequiredValue(_scan.Next(next)) is { } required && required.Past >= pieceEnd)
            {
                Run(required.Specifier, line, null, [], export: false);
                continue;
            }

            if (key is not null && next >= pieceEnd)
            {
                _exported.Add((key, key, line));
            }
            else if (key is not null && _scan.Is(next, ":") && _scan.IsName(_scan.Next(next)) && _scan.Next(_scan.Next(next)) >= pieceEnd)
            {
                _exported.Add((_scan.Text(_scan.Next(next)), key, line));
            }
            else if (key is not null && _scan.Is(next, ":") && FunctionAt(_scan.Next(next), pieceEnd) is { } function)
            {
                Bound(pieceStart, pieceStart, _scan.Next(next), key, function, frame, [], [], [], "function", seenIn: null, exports: null);
            }
            else if (MethodNameAt(pieceStart, pieceEnd) is var methodAt and >= 0 && FunctionAt(methodAt, pieceEnd, methodName: true) is { } method)
            {
                Bound(pieceStart, pieceStart, methodAt, _scan.Text(methodAt), method, frame, [], [], [], "function", seenIn: null, exports: null);
            }
            else
            {
                // Any other value - a call's result, a spread of a name: what it calls is the module's.
                var from = key is not null && _scan.Is(next, ":") ? _scan.Next(next) : pieceStart;
                var to = pieceEnd;
                Defer(() => Calls(_text, from, to, null, new Locals(), null, depth: 0));
            }
        }
    }

    /// <summary>
    /// Where the name of a method an object literal writes from <paramref name="at"/> stands - past <c>async</c>,
    /// <c>get</c>, <c>set</c> and a generator's <c>*</c>, its parameters after it - or -1 where none is written there.
    /// </summary>
    private int MethodNameAt(int at, int end)
    {
        while (at < end && (_scan.Is(at, "async") || _scan.Is(at, "get") || _scan.Is(at, "set") || _scan.Is(at, "*")) && !_scan.Is(_scan.Next(at), "("))
        {
            at = _scan.Next(at);
        }

        return at < end && _scan.IsName(at) && _scan.Is(_scan.Next(at), "(") ? at : -1;
    }

    // ---- Decorators ----

    /// <summary>
    /// Past the decorators at <paramref name="at"/> - <c>@name</c>, <c>@a.b</c>, each with its type's arguments and its
    /// own - each named among <paramref name="marks"/> and its span among <paramref name="spans"/> where they are given.
    /// </summary>
    private int Decorators(int at, int end, List<string>? marks, List<(int Start, int End)>? spans)
    {
        while (at < end && _scan.Is(at, "@") && _scan.IsName(_scan.Next(at)))
        {
            var (names, past) = ChainAt(_scan, _scan.Next(at), end);

            if (_typescript && _scan.Is(past, "<"))
            {
                past = Angle(_scan, past, end);
            }

            if (_scan.Is(past, "("))
            {
                past = _scan.Past(past);
            }

            marks?.Add("@" + string.Join('.', names.Select(index => _scan.Text(index))));
            spans?.Add((at, past));
            at = past;
        }

        return at;
    }

    /// <summary>What each decorator of <paramref name="decorators"/> calls, from the node of <paramref name="key"/>: one written with its arguments a call, one written alone a call of what it names.</summary>
    private void DecoratorCalls(string key, List<(int Start, int End)> decorators, string? owner)
    {
        foreach (var (start, end) in decorators)
        {
            var (names, past) = ChainAt(_scan, _scan.Next(start), end);

            if (past < end)
            {
                Calls(_text, start + 1, end, key, new Locals(), owner, depth: 0);
                continue;
            }

            var written = names.Select(index => _scan.Text(index)).ToList();
            var (name, qualifier) = written.Count == 1 ? Callee(written[0]) : (written[^1], ChainQualifier(written[..^1], null, out _));
            _extract.Reference(key, CodeEdgeKinds.Calls, name, _scan[names[^1]].Line, qualifier: qualifier);
        }
    }

    // ---- Types ----

    /// <summary>
    /// A class at <paramref name="at"/>'s <c>class</c>: a type node over its head and body named by its name -
    /// <paramref name="named"/> where a binding or an export names it - its clauses' edges, its decorators' calls, and its
    /// members read in its own scope; <paramref name="nameIsDefault"/> where it is a module's default export, and
    /// <paramref name="bindingNames"/> where the binding's name is its name whatever its own.
    /// </summary>
    private int Class(int start, int head, int at, int end, ScanFrame frame, List<string> modifiers, List<string> marks, List<(int Start, int End)> decorators, string? seenIn, string? named, bool nameIsDefault, bool ambient, bool bindingNames = false)
    {
        var cursor = _scan.Next(at);
        string name;

        if (_scan.IsName(cursor) && !_scan.Is(cursor, "extends") && !_scan.Is(cursor, "implements"))
        {
            // const A = class B {}: the class is A's, its own name its body's alone.
            name = bindingNames && named is not null ? named : _scan.Text(cursor);
            cursor = _scan.Next(cursor);
        }
        else if (named is not null)
        {
            name = named;
        }
        else
        {
            return StatementEnd(at, end);
        }

        var typeParameters = _typescript && _scan.Is(cursor, "<") ? TypeParameters(cursor, end) : [];

        if (_typescript && _scan.Is(cursor, "<"))
        {
            cursor = Angle(_scan, cursor, end);
        }

        var open = BodyOpen(cursor, end);

        if (open < 0)
        {
            return StatementEnd(at, end);
        }

        var (inner, close) = _scan.Bounds(open, end);
        if (nameIsDefault)
        {
            _exported.Add((name, "default", _scan[start].Line));
        }

        var key = DeclareType(start, head, Math.Min(inner, _scan.Count - 1), frame, name, "class", modifiers, marks, signatureEnd: open, seenIn);
        var added = typeParameters.Where(_typeParameters.Add).ToList();

        try
        {
            Clauses(key, cursor, open);
            DecoratorCalls(key, decorators, frame.Owner);
            Members(open, inner, frame.Inside(frame.Qualify(name), frame.Qualify(name), key, name), MemberPlace.Class, ambient);
        }
        finally
        {
            _typeParameters.ExceptWith(added);
        }

        return close;
    }

    /// <summary>An interface named at <paramref name="nameAt"/>: a type node - its parts in the file one - its <c>extends</c> an <c>inherits</c> edge each, and its members, each without a body.</summary>
    private int Interface(int start, int head, int nameAt, int end, ScanFrame frame, List<string> modifiers, List<string> marks, string? seenIn)
    {
        var name = _scan.Text(nameAt);
        var cursor = _scan.Next(nameAt);
        var typeParameters = _scan.Is(cursor, "<") ? TypeParameters(cursor, end) : [];

        if (_scan.Is(cursor, "<"))
        {
            cursor = Angle(_scan, cursor, end);
        }

        var open = BodyOpen(cursor, end);

        if (open < 0)
        {
            return StatementEnd(head, end);
        }

        var (inner, close) = _scan.Bounds(open, end);
        var key = _extract.Part(TypeNode(start, head, Math.Min(inner, _scan.Count - 1), frame, name, "interface", modifiers, marks, open, seenIn));
        Registered(key, frame, name);
        var added = typeParameters.Where(_typeParameters.Add).ToList();

        try
        {
            Clauses(key, cursor, open);
            Members(open, inner, frame.Inside(frame.Qualify(name), frame.Qualify(name), key, name), MemberPlace.Interface, ambient: true);
        }
        finally
        {
            _typeParameters.ExceptWith(added);
        }

        return close;
    }

    /// <summary>An enum named at <paramref name="nameAt"/>: a type node, each of its members a constant of its type.</summary>
    private int Enum(int start, int head, int nameAt, int end, ScanFrame frame, List<string> modifiers, List<string> marks, string? seenIn)
    {
        var name = _scan.Text(nameAt);
        var open = BodyOpen(_scan.Next(nameAt), end);

        if (open < 0)
        {
            return StatementEnd(head, end);
        }

        var (inner, close) = _scan.Bounds(open, end);
        var key = DeclareType(start, head, Math.Min(inner, _scan.Count - 1), frame, name, "enum", modifiers, marks, signatureEnd: open, seenIn);
        var inside = frame.Inside(frame.Qualify(name), frame.Qualify(name), key, name);

        foreach (var (pieceStart, pieceEnd) in ScanCalls.Split(_scan, open + 1, inner, _types))
        {
            var member = _scan.IsName(pieceStart) ? _scan.Text(pieceStart) : IsText(pieceStart) ? Unquoted(_scan.Text(pieceStart)) : null;

            if (member is not { Length: > 0 })
            {
                continue;
            }

            _extract.Add(new ExtractNode(Id(inside.Qualify(member)), CodeNodeKinds.Member, member, inside.Qualify(member), _scan[pieceStart].Line, _scan[LastOf(pieceEnd)].EndLine, Signature(pieceStart, pieceEnd))
            {
                SubKind = "constant",
                Scope = inside.Scope,
                Parent = key,
                Summary = ScanComments.Summary(_scan, pieceStart),
                ValueType = name,
            });
        }

        return close;
    }

    /// <summary>A type alias named at <paramref name="nameAt"/>: a type node over its statement, referencing what it names.</summary>
    private int TypeAlias(int start, int head, int nameAt, int end, ScanFrame frame, List<string> modifiers, List<string> marks, string? seenIn)
    {
        var stop = StatementEnd(nameAt, end);
        var last = LastOf(stop);
        var name = _scan.Text(nameAt);
        var cursor = _scan.Next(nameAt);
        var typeParameters = _scan.Is(cursor, "<") ? TypeParameters(cursor, stop) : [];

        if (_scan.Is(cursor, "<"))
        {
            cursor = Angle(_scan, cursor, stop);
        }

        var aliasEnd = _scan.Is(last, ";") ? last : stop;
        var key = DeclareType(start, head, last, frame, name, "type", modifiers, marks, signatureEnd: _scan.Is(cursor, "=") ? cursor : aliasEnd, seenIn);
        var added = typeParameters.Where(_typeParameters.Add).ToList();

        try
        {
            if (_scan.Is(cursor, "="))
            {
                TypeReferences(key, _scan.Next(cursor), aliasEnd);
            }
        }
        finally
        {
            _typeParameters.ExceptWith(added);
        }

        return stop;
    }

    /// <summary>
    /// A namespace or a module block named at <paramref name="nameAt"/> - <c>A.B</c>, or a string an ambient module is
    /// named by - an open scope of its name, nested a node for each name of it, its parts in the file one: its statements
    /// read in it, those it does not export seen in it alone but in an ambient one, where all are.
    /// </summary>
    private int Namespace(int start, int head, int nameAt, int end, ScanFrame frame, List<string> modifiers, List<string> marks, string? seenIn, bool ambient)
    {
        List<string> names;
        int cursor;

        if (IsText(nameAt))
        {
            names = [_scan.Text(nameAt)];
            cursor = _scan.Next(nameAt);
        }
        else
        {
            var (chain, past) = ChainAt(_scan, nameAt, end);
            names = [.. chain.Select(index => _scan.Text(index))];
            cursor = past;
        }

        if (!_scan.Is(cursor, "{"))
        {
            return StatementEnd(head, end);
        }

        var (inner, close) = _scan.Bounds(cursor, end);
        var inside = frame;
        var subKind = IsText(nameAt) ? "module" : "namespace";
        var outermost = true;

        foreach (var name in names)
        {
            var qualified = inside.Qualify(name);
            var key = _extract.Part(new ExtractNode(Id(qualified), CodeNodeKinds.Namespace, name, qualified, _scan[start].Line, _scan[Math.Min(inner, _scan.Count - 1)].EndLine, Signature(head, cursor))
            {
                SubKind = subKind,
                Scope = inside.Scope,
                Parent = inside.Parent,
                Summary = ScanComments.Summary(_scan, start),
                Modifiers = [.. modifiers, .. marks],
                SeenIn = outermost ? seenIn : null,
            });

            if (outermost)
            {
                Registered(key, frame, name);
            }

            _extract.Scope(qualified, open: true, parent: inside.Scope);
            inside = inside.Inside(qualified, qualified, key, null);
            outermost = false;
        }

        if (inside.TooDeep)
        {
            _extract.Dynamic(_scan[cursor].Line, ScanFrame.TooDeepWhy);
            return close;
        }

        var hidden = ambient ? null : inside.Scope;
        ScanFrame.Walk(cursor + 1, inner, at => Statement(at, inner, inside, hidden));
        return close;
    }

    /// <summary>A <c>declare global</c> block at <paramref name="open"/>: its declarations the repository's, at the top of no scope.</summary>
    private int Global(int open, int end, ScanFrame frame)
    {
        var (inner, close) = _scan.Bounds(open, end);
        var global = frame.Inside(null, null, null, null);

        if (global.TooDeep)
        {
            _extract.Dynamic(_scan[open].Line, ScanFrame.TooDeepWhy);
            return close;
        }

        ScanFrame.Walk(open + 1, inner, at => Statement(at, inner, global, null));
        return close;
    }

    /// <summary>A type node over the tokens from <paramref name="start"/> to <paramref name="last"/>, its signature from <paramref name="head"/> before <paramref name="signatureEnd"/>; answers its key.</summary>
    private string DeclareType(int start, int head, int last, ScanFrame frame, string name, string subKind, List<string> modifiers, List<string> marks, int signatureEnd, string? seenIn)
    {
        var key = _extract.Add(TypeNode(start, head, last, frame, name, subKind, modifiers, marks, signatureEnd, seenIn));
        Registered(key, frame, name);
        return key;
    }

    private ExtractNode TypeNode(int start, int head, int last, ScanFrame frame, string name, string subKind, List<string> modifiers, List<string> marks, int signatureEnd, string? seenIn)
        => new(Id(frame.Qualify(name)), CodeNodeKinds.Type, name, frame.Qualify(name), _scan[start].Line, _scan[Math.Clamp(last, start, _scan.Count - 1)].EndLine, Signature(head, signatureEnd))
        {
            SubKind = subKind,
            Scope = frame.Scope,
            Parent = frame.Parent,
            BodyStart = _scan.Is(signatureEnd, "{") ? _scan[signatureEnd].Line : null,
            Summary = ScanComments.Summary(_scan, start),
            Modifiers = [.. modifiers, .. marks],
            SeenIn = seenIn,
        };

    /// <summary>Notes that the node of <paramref name="key"/>, named <paramref name="name"/>, stands at the file's top where <paramref name="frame"/> is the top's: what an export list names.</summary>
    private void Registered(string key, ScanFrame frame, string name)
    {
        if (frame.Parent is null)
        {
            (_topKeys.TryGetValue(name, out var keys) ? keys : _topKeys[name] = []).Add(key);
        }
    }

    /// <summary>
    /// A type's clauses from <paramref name="at"/> to <paramref name="end"/>: <c>extends</c> an <c>inherits</c> edge to
    /// each type it names - a call it writes instead, a mixin's, read as calls - and <c>implements</c> an
    /// <c>implements</c> edge each, each type's arguments referenced.
    /// </summary>
    private void Clauses(string key, int at, int end)
    {
        string? kind = null;

        while (at < end)
        {
            if (_scan.Is(at, "extends"))
            {
                kind = CodeEdgeKinds.Inherits;
            }
            else if (_scan.Is(at, "implements"))
            {
                kind = CodeEdgeKinds.Implements;
            }
            else if (kind is not null && _scan.IsName(at))
            {
                var (chain, past) = Chain(_scan, at, end);

                if (_scan.Is(past, "("))
                {
                    var call = Math.Min(_scan.Past(past), end);
                    var from = at;
                    Defer(() => Calls(_text, from, call, key, new Locals(), null, depth: 0));
                    at = call;
                    continue;
                }

                if (!Builtins.Contains(chain!))
                {
                    Mention(key, kind, chain!, _scan[at].Line);
                }

                if (_typescript && _scan.Is(past, "<"))
                {
                    var arguments = Angle(_scan, past, end);
                    TypeReferences(key, past + 1, arguments - 1);
                    past = arguments;
                }

                at = past;
                continue;
            }

            at = _scan.Next(at);
        }
    }

    /// <summary>Where a member is declared: what tells its kinds apart.</summary>
    private enum MemberPlace
    {
        /// <summary>A class's body.</summary>
        Class,

        /// <summary>An interface's body, whose members have no bodies.</summary>
        Interface,
    }

    /// <summary>The members of the body <paramref name="open"/> opens, read in <paramref name="frame"/>, their bodies read once every member is declared.</summary>
    private void Members(int open, int inner, ScanFrame frame, MemberPlace place, bool ambient)
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
            ScanFrame.Walk(open + 1, inner, at => Member(at, inner, frame, place, ambient));

            for (var at = 0; at < _bodies.Count; at++)
            {
                _bodies[at]();
            }
        }
        finally
        {
            _bodies = outer;
        }
    }

    // ---- Members ----

    /// <summary>
    /// A member at <paramref name="at"/>, past its decorators and modifiers: a constructor, a method, a getter, a setter, a
    /// field - one an arrow or a function is given a method - or a static block, whose calls are its type's; a call, a
    /// construct or an index signature, and a member a computed name names, are no node.
    /// </summary>
    private int Member(int at, int end, ScanFrame frame, MemberPlace place, bool ambient)
    {
        var start = at;
        var marks = new List<string>();
        var decorators = new List<(int Start, int End)>();
        at = Decorators(at, end, marks, decorators);

        if (at >= end)
        {
            return end;
        }

        if (_scan.Is(at, ";") || _scan.Is(at, ","))
        {
            return at + 1;
        }

        var head = at;
        var modifiers = new List<string>();

        while (_scan.IsName(at) && MemberModifiers.Contains(_scan.Text(at)) && !IsMemberName(at))
        {
            modifiers.Add(_scan.Text(at));
            at = _scan.Next(at);
        }

        // static { ... }: what it calls is its type's.
        if (modifiers is ["static"] && _scan.Is(at, "{"))
        {
            var (inner, close) = _scan.Bounds(at, end);
            var block = at;
            var holder = frame.Parent!;
            Defer(() => Calls(_text, block + 1, inner, holder, new Locals(), frame.Owner, depth: 0));
            return close;
        }

        if (_scan.Is(at, "*"))
        {
            modifiers.Add("*");
            at = _scan.Next(at);
        }

        var subKind = "method";

        if ((_scan.Is(at, "get") || _scan.Is(at, "set")) && !IsMemberName(at))
        {
            subKind = _scan.Is(at, "get") ? "getter" : "setter";
            at = _scan.Next(at);
        }

        // A call or a construct signature, an index signature, a computed name: no node.
        if (_scan.Is(at, "(") || _scan.Is(at, "<") || _scan.Is(at, "[") || (_scan.Is(at, "new") && _scan.Is(_scan.Next(at), "(")))
        {
            return Unnamed(at, end, frame);
        }

        var name = _scan.IsName(at) ? _scan.Text(at)
            : IsText(at) ? Unquoted(_scan.Text(at))
            : _scan[at].Kind == ScanTokenKind.Number ? _scan.Text(at)
            : null;

        if (name is not { Length: > 0 })
        {
            return StatementEnd(at, end, commas: place == MemberPlace.Interface, typed: place == MemberPlace.Interface);
        }

        var nameAt = at;
        var next = _scan.Next(at);

        if (_scan.Is(next, "?") || _scan.Is(next, "!"))
        {
            next = _scan.Next(next);
        }

        // A private member - #name, or TypeScript's private - is its type's alone.
        var seenIn = name.StartsWith('#') || modifiers.Contains("private", StringComparer.Ordinal) ? frame.Scope : null;

        if (name == "constructor" && place == MemberPlace.Class && subKind == "method" && _scan.Is(next, "("))
        {
            return Constructor(start, head, nameAt, next, end, frame, marks, decorators, modifiers, ambient);
        }

        if (_scan.Is(next, "(") || (_typescript && _scan.Is(next, "<")))
        {
            var noBody = place == MemberPlace.Interface || ambient;
            var function = Declared(next, end, noBody);

            if (function is null)
            {
                return StatementEnd(at, end, commas: place == MemberPlace.Interface, typed: place == MemberPlace.Interface);
            }

            var shown = subKind == "setter" ? name + "=" : name;
            return Bound(start, head, nameAt, shown, function, frame, marks, decorators, modifiers, subKind, seenIn, exports: null);
        }

        return Field(start, head, nameAt, name, next, end, frame, marks, decorators, modifiers, place, ambient, seenIn);
    }

    /// <summary>
    /// Whether the word at <paramref name="at"/> is the name a member declares, not one of its modifiers: what follows it
    /// ends a name - its parameters, a type, a value, an optional mark - or a line break does.
    /// </summary>
    private bool IsMemberName(int at)
    {
        var next = _scan.Next(at);

        return next >= _scan.Count || _scan.Is(next, "(") || _scan.Is(next, "=") || _scan.Is(next, ";") || _scan.Is(next, ":") || _scan.Is(next, "?")
            || _scan.Is(next, "!") || _scan.Is(next, "<") || _scan.Is(next, "}") || _scan.Is(next, ",") || _scan[next].Line > _scan[at].EndLine;
    }

    /// <summary>A member no name of its own names, at <paramref name="at"/>: passed over to its end, what a body of it calls its type's.</summary>
    private int Unnamed(int at, int end, ScanFrame frame)
    {
        var cursor = _scan.Is(at, "[") ? _scan.Past(at) : at;

        if (_scan.Is(cursor, "?"))
        {
            cursor = _scan.Next(cursor);
        }

        if ((_scan.Is(cursor, "(") || _scan.Is(cursor, "<")) && Declared(cursor, end, noBody: false) is { Body: >= 0 } function)
        {
            var holder = frame.Parent!;
            var locals = new Locals(function.Parameters);
            Defer(() => Calls(_text, function.Body + 1, function.Last + (function.Arrow ? 1 : 0), holder, locals, frame.Owner, depth: 0));
            return function.Last + 1;
        }

        return StatementEnd(at, end, commas: true, typed: true);
    }

    /// <summary>
    /// A field named <paramref name="name"/> at <paramref name="nameAt"/>, its type or its value from
    /// <paramref name="at"/>: a member node of the type it is declared with or its value makes, what its value calls - a
    /// method where an arrow or a function is its value.
    /// </summary>
    private int Field(int start, int head, int nameAt, string name, int at, int end, ScanFrame frame, List<string> marks, List<(int Start, int End)> decorators, List<string> modifiers, MemberPlace place, bool ambient, string? seenIn)
    {
        var stop = StatementEnd(nameAt, end, commas: place == MemberPlace.Interface, typed: place == MemberPlace.Interface || (_typescript && _scan.Is(at, ":")));
        var last = LastOf(stop);
        var boundary = _scan.Is(last, ";") || _scan.Is(last, ",") ? last : stop;
        var typeStart = _typescript && _scan.Is(at, ":") ? _scan.Next(at) : -1;
        var assign = typeStart >= 0 ? ScanCalls.TopLevel(_scan, typeStart, boundary, "=", _types) : _scan.Is(at, "=") ? at : boundary;
        var typeEnd = typeStart >= 0 ? assign : -1;

        if (assign < boundary && FunctionAt(_scan.Next(assign), boundary) is { } function)
        {
            Bound(start, head, nameAt, name, function, frame, marks, decorators, modifiers, "method", seenIn, exports: null);
            return stop;
        }

        var key = _extract.Add(new ExtractNode(Id(frame.Qualify(name)), CodeNodeKinds.Member, name, frame.Qualify(name), _scan[start].Line, _scan[last].EndLine, Signature(head, assign < boundary ? assign : boundary))
        {
            SubKind = place == MemberPlace.Interface ? "property" : "field",
            Scope = frame.Scope,
            Parent = frame.Parent,
            Summary = ScanComments.Summary(_scan, start),
            Modifiers = [.. modifiers, .. marks],
            Prototype = _declarations,
            ValueType = ValueTypeOf(typeStart, typeEnd, assign, boundary),
            SeenIn = seenIn,
        });

        if (typeStart >= 0 && IsFunctionType(typeStart, typeEnd) && frame.Owner is { } owner)
        {
            (_functionFields.TryGetValue(owner, out var fields) ? fields : _functionFields[owner] = new(StringComparer.Ordinal)).Add(name);
        }

        if (typeStart >= 0)
        {
            TypeReferences(key, typeStart, typeEnd);
        }

        Overriding(key, name, nameAt, modifiers);
        DecoratorCalls(key, decorators, frame.Owner);

        if (assign < boundary && !ambient)
        {
            var value = _scan.Next(assign);
            Defer(() => Calls(_text, value, boundary, key, new Locals(), frame.Owner, depth: 0));
        }

        return stop;
    }

    /// <summary>
    /// A constructor at <paramref name="nameAt"/>, its parameters at <paramref name="open"/>: a member node named
    /// <c>constructor</c>, each parameter property a field of its type, and what its body calls.
    /// </summary>
    private int Constructor(int start, int head, int nameAt, int open, int end, ScanFrame frame, List<string> marks, List<(int Start, int End)> decorators, List<string> modifiers, bool ambient)
    {
        if (Declared(open, end, ambient) is not { } function)
        {
            return StatementEnd(nameAt, end);
        }

        Callable(start, head, nameAt, "constructor", function, frame, marks, decorators, modifiers, "constructor", seenIn: null, exports: null);

        foreach (var property in function.Parameters.Properties)
        {
            var name = _scan.Text(property.NameAt);
            var fieldKey = _extract.Add(new ExtractNode(Id(frame.Qualify(name)), CodeNodeKinds.Member, name, frame.Qualify(name), _scan[property.Start].Line, _scan[LastOf(property.End)].EndLine, Signature(property.Start, property.End))
            {
                SubKind = "field",
                Scope = frame.Scope,
                Parent = frame.Parent,
                Modifiers = property.Modifiers,
                ValueType = property.TypeStart >= 0 ? TypeKey(_scan, property.TypeStart, property.TypeEnd) : null,
                SeenIn = property.Modifiers.Contains("private", StringComparer.Ordinal) ? frame.Scope : null,
            });

            if (property.TypeStart >= 0)
            {
                TypeReferences(fieldKey, property.TypeStart, property.TypeEnd);

                if (IsFunctionType(property.TypeStart, property.TypeEnd) && frame.Owner is { } owner)
                {
                    (_functionFields.TryGetValue(owner, out var fields) ? fields : _functionFields[owner] = new(StringComparer.Ordinal)).Add(name);
                }
            }
        }

        return function.Last + 1;
    }

    /// <summary>
    /// An <c>overrides</c> edge from the member of <paramref name="key"/>, named at <paramref name="nameAt"/>, to the one of
    /// its name its bases declare, where TypeScript's <c>override</c> marks it.
    /// </summary>
    private void Overriding(string key, string name, int nameAt, List<string> modifiers)
    {
        if (modifiers.Contains("override", StringComparer.Ordinal))
        {
            _extract.Reference(key, CodeEdgeKinds.Overrides, name, _scan[nameAt].Line, qualifier: new ExtractQualifier(CodeQualifierKinds.Base));
        }
    }

    // ---- Functions ----

    /// <summary>
    /// A function's shape: its parameters, its return type's span - -1 twice where it writes none - the index of its body's
    /// <c>{</c> or arrow - -1 where it has none - its last token, whether its body is an arrow's expression, where its
    /// signature ends, and the name it is written with, where an expression writes one (-1 otherwise).
    /// </summary>
    private sealed record FunctionShape(Parameters Parameters, int ReturnStart, int ReturnEnd, int Body, int Last, bool Arrow, int SignatureEnd, int Name = -1)
    {
        /// <summary>The names of its own type parameters: no type the repository declares, in its signature and its body.</summary>
        public IReadOnlyList<string> Own { get; init; } = [];
    }

    /// <summary>
    /// A declared function's shape from <paramref name="at"/> - its type parameters, its parameters, its return type and
    /// its body, a block - or, where <paramref name="noBody"/> or none follows, its signature alone;
    /// <see langword="null"/> where no parameters are there.
    /// </summary>
    private FunctionShape? Declared(int at, int end, bool noBody)
    {
        IReadOnlyList<string> own = [];

        if (_typescript && _scan.Is(at, "<"))
        {
            own = TypeParameters(at, end);
            at = Angle(_scan, at, end);
        }

        if (!_scan.Is(at, "(") || _scan.Match(at) is not (var close and > 0) || close >= end)
        {
            return null;
        }

        var parameters = ParametersOf(_scan, at, close);
        var after = _scan.Next(close);
        var (returnStart, returnEnd) = (-1, -1);

        if (_typescript && _scan.Is(after, ":"))
        {
            returnStart = _scan.Next(after);
            returnEnd = TypeEnd(_scan, returnStart, end);
            after = returnEnd;
        }

        if (!noBody && _scan.Is(after, "{") && after < end)
        {
            var inner = Math.Min(_scan.Bounds(after, end).Inner, _scan.Count - 1);
            return new FunctionShape(parameters, returnStart, returnEnd, after, inner, Arrow: false, SignatureEnd: after) { Own = own };
        }

        // A signature alone ends at its ';', else at its last token.
        var last = after < end && _scan.Is(after, ";") ? after : Math.Max(close, _scan.Previous(Math.Min(after, _scan.Count)));
        return new FunctionShape(parameters, returnStart, returnEnd, -1, last, Arrow: false, SignatureEnd: Math.Min(after, last + 1)) { Own = own };
    }

    /// <summary>
    /// The function expression that begins at <paramref name="at"/> and ends by <paramref name="end"/>, where one does - a
    /// <c>function</c>, an arrow, either <c>async</c> - or, where <paramref name="methodName"/>, an object's method by its
    /// name; <see langword="null"/> where none is there.
    /// </summary>
    private FunctionShape? FunctionAt(int at, int end, bool methodName = false)
    {
        if (at >= end)
        {
            return null;
        }

        if (methodName)
        {
            return Declared(_scan.Next(at), end, noBody: false) is { Body: >= 0 } method ? method : null;
        }

        if (_scan.Is(at, "async") && _scan.Next(at) < end)
        {
            at = _scan.Next(at);
        }

        if (_scan.Is(at, "function"))
        {
            var next = FunctionName(_scan, at).Name;
            var name = _scan.IsName(next) ? next : -1;

            if (name >= 0)
            {
                next = _scan.Next(next);
            }

            return Declared(next, end, noBody: false) is { Body: >= 0 } function ? function with { Name = name } : null;
        }

        // x => ...
        if (_scan.IsName(at) && _scan.Is(_scan.Next(at), "=>"))
        {
            var single = new Parameters(_typescript ? 1 : 0, _typescript ? 1 : null, [], new Dictionary<string, string?>(StringComparer.Ordinal) { [_scan.Text(at)] = null }, new HashSet<string>(StringComparer.Ordinal), []);
            return Arrowed(single, -1, -1, _scan.Next(at), end);
        }

        var open = at;
        IReadOnlyList<string> own = [];

        if (_typescript && _scan.Is(open, "<"))
        {
            own = TypeParameters(open, end);
            open = Angle(_scan, open, end);
        }

        if (!_scan.Is(open, "(") || _scan.Match(open) is not (var close and > 0) || close >= end)
        {
            return null;
        }

        var after = _scan.Next(close);
        var (returnStart, returnEnd) = (-1, -1);

        if (_typescript && _scan.Is(after, ":"))
        {
            returnStart = _scan.Next(after);
            returnEnd = TypeEnd(_scan, returnStart, end);
            after = returnEnd;
        }

        return _scan.Is(after, "=>") && after < end ? Arrowed(ParametersOf(_scan, open, close), returnStart, returnEnd, after, end) with { Own = own } : null;
    }

    /// <summary>An arrow function's shape, its arrow at <paramref name="arrow"/>: a block's body to its brace, an expression's to <paramref name="end"/>.</summary>
    private FunctionShape Arrowed(Parameters parameters, int returnStart, int returnEnd, int arrow, int end)
    {
        var body = _scan.Next(arrow);

        if (_scan.Is(body, "{") && body < end && _scan.Bounds(body, end).Past is var past && (past >= end || IsEnded(past, end) || _scan.Is(past, ",") || _scan.Is(past, ")")))
        {
            return new FunctionShape(parameters, returnStart, returnEnd, body, Math.Min(_scan.Bounds(body, end).Inner, _scan.Count - 1), Arrow: false, SignatureEnd: arrow);
        }

        return new FunctionShape(parameters, returnStart, returnEnd, arrow, Math.Max(arrow, LastOf(Math.Min(end, _scan.Count))), Arrow: true, SignatureEnd: arrow);
    }

    /// <summary>A <c>function</c> declaration at <paramref name="at"/>: a member node of its name - <c>default</c> where a default export's has none.</summary>
    private int Function(int start, int head, int at, int end, ScanFrame frame, List<string> modifiers, List<string> marks, List<(int Start, int End)> decorators, string? seenIn, bool isDefault, bool ambient)
    {
        var (next, generator) = FunctionName(_scan, at);

        if (generator)
        {
            modifiers.Add("*");
        }

        string name;
        var nameAt = next;

        if (_scan.IsName(next) && !Reserved.Contains(_scan.Text(next)))
        {
            name = _scan.Text(next);
            next = _scan.Next(next);
        }
        else if (isDefault)
        {
            name = "default";
            nameAt = at;
        }
        else
        {
            return StatementEnd(at, end);
        }

        if (Declared(next, end, ambient) is not { } function)
        {
            return StatementEnd(at, end);
        }

        if (isDefault)
        {
            _exported.Add((name, "default", _scan[start].Line));
        }

        return Bound(start, head, nameAt, name, function, frame, marks, decorators, modifiers, "function", seenIn, exports: null);
    }

    /// <summary>
    /// The variables a <c>const</c>, <c>let</c> or <c>var</c> at <paramref name="at"/> declares: each a function where a
    /// function or an arrow is its value, a class where a class is, and a member node of the type it is declared with or its
    /// value makes otherwise, what its value calls; one a <c>require</c> binds is an import, and no node.
    /// </summary>
    private int Variables(int start, int head, int at, int end, ScanFrame frame, List<string> modifiers, List<string> marks, List<(int Start, int End)> decorators, string? seenIn, bool ambient)
    {
        var word = _scan.Text(at);
        var stop = StatementEnd(head, end);
        var last = LastOf(stop);
        var boundary = _scan.Is(last, ";") ? last : stop;
        var named = new List<string>(modifiers) { word };

        foreach (var (pieceStart, pieceEnd) in Declarators(_scan.Next(at), boundary))
        {
            // A declarator after the first begins where it is written, its header its own: const a = 1, b = () => 2.
            var (begins, header) = pieceStart == _scan.Next(at) ? (start, head) : (pieceStart, pieceStart);

            if (!_scan.IsName(pieceStart))
            {
                // A pattern binds names of no declaration: what its value calls is the module's.
                var value = ScanCalls.TopLevel(_scan, pieceStart, pieceEnd, "=", _types);

                if (value < pieceEnd && RequireDeclared(pieceStart, pieceEnd) is null)
                {
                    var from = _scan.Next(value);
                    var to = pieceEnd;
                    Defer(() => Calls(_text, from, to, null, new Locals(), null, depth: 0));
                }

                continue;
            }

            var nameAt = pieceStart;
            var name = _scan.Text(nameAt);
            var cursor = _scan.Next(nameAt);

            if (_scan.Is(cursor, "!"))
            {
                cursor = _scan.Next(cursor);
            }

            var typeStart = _typescript && _scan.Is(cursor, ":") ? _scan.Next(cursor) : -1;
            var assign = ScanCalls.TopLevel(_scan, typeStart >= 0 ? typeStart : cursor, pieceEnd, "=", _types);
            var typeEnd = typeStart >= 0 ? assign : -1;
            var valueAt = assign < pieceEnd ? _scan.Next(assign) : -1;

            if (RequireDeclared(pieceStart, pieceEnd) is not null)
            {
                continue;
            }

            if (valueAt >= 0 && FunctionAt(valueAt, pieceEnd) is { } function)
            {
                Bound(begins, header, nameAt, name, function, frame, marks, decorators, named, "function", seenIn, exports: null);
                continue;
            }

            if (valueAt >= 0 && _scan.Is(valueAt, "class"))
            {
                Class(begins, header, valueAt, pieceEnd, frame, named, marks, decorators, seenIn, name, nameIsDefault: false, ambient, bindingNames: true);
                continue;
            }

            var key = _extract.Add(new ExtractNode(Id(frame.Qualify(name)), CodeNodeKinds.Member, name, frame.Qualify(name), _scan[begins].Line, _scan[LastOf(pieceEnd)].EndLine, ExtractText.Signature(ExtractText.Flat($"{_scan.Spelled(head, _scan.Next(at))} {_scan.Spelled(nameAt, assign)}")))
            {
                SubKind = word == "const" ? "constant" : "variable",
                Scope = frame.Scope,
                Parent = frame.Parent,
                Summary = ScanComments.Summary(_scan, begins),
                Modifiers = [.. named, .. marks],
                Prototype = ambient,
                ValueType = ValueTypeOf(typeStart, typeEnd, assign, pieceEnd),
                SeenIn = seenIn,
            });

            Registered(key, frame, name);

            if (frame.Parent is null && !(typeStart >= 0 && IsFunctionType(typeStart, typeEnd)))
            {
                _values.Add(name);
            }

            if (typeStart >= 0)
            {
                TypeReferences(key, typeStart, typeEnd);
            }

            if (valueAt >= 0 && !ambient)
            {
                var to = pieceEnd;
                Defer(() => Calls(_text, valueAt, to, key, new Locals(), null, depth: 0));
            }
        }

        return stop;
    }

    /// <summary>
    /// The declarators from <paramref name="from"/> to <paramref name="to"/>, split at their top-level commas: groups
    /// passed over, and a list of type arguments or of a generic arrow's type parameters (<c>&lt;T,&gt;(x: T) =&gt; x</c>).
    /// </summary>
    private List<(int Start, int End)> Declarators(int from, int to)
    {
        var pieces = new List<(int Start, int End)>();
        var start = -1;

        for (var at = from; at < to && at < _scan.Count; at++)
        {
            if (_scan.Is(at, ","))
            {
                if (start >= 0)
                {
                    pieces.Add((start, at));
                }

                start = -1;
                continue;
            }

            start = start < 0 ? at : start;

            if (_scan.Opens(at))
            {
                at = Math.Max(at, Math.Min(_scan.Past(at), to) - 1);
            }
            else if (_types is not null && _scan.Is(at, "<") && ScanCalls.TypeArgumentsEnd(_scan, at, _types) is var past and > 0 && past <= to
                && (_scan.IsName(_scan.Previous(at)) || _scan.Is(past, "(")))
            {
                at = past - 1;
            }
        }

        if (start >= 0)
        {
            pieces.Add((start, Math.Min(to, _scan.Count)));
        }

        return pieces;
    }

    /// <summary>The type a value is declared with - its annotation's from <paramref name="typeStart"/> - or that its value from past <paramref name="assign"/> makes; <see langword="null"/> where neither says one.</summary>
    private string? ValueTypeOf(int typeStart, int typeEnd, int assign, int end)
        => typeStart >= 0 ? TypeKey(_scan, typeStart, typeEnd) is { } declared and not "any" ? declared : null
            : assign < end ? Made(_scan, _scan.Next(assign), end)
            : null;

    /// <summary>
    /// A function, a method, an accessor or an arrow bound to a name, named <paramref name="name"/> at
    /// <paramref name="nameAt"/>, of <paramref name="function"/>'s shape: a member node over its head and body, what its
    /// signature names, the member it overrides, its decorators' calls and what its body calls. Answers where the next
    /// declaration begins.
    /// </summary>
    private int Bound(int start, int head, int nameAt, string name, FunctionShape function, ScanFrame frame, List<string> marks, List<(int Start, int End)> decorators, List<string> modifiers, string subKind, string? seenIn, IReadOnlyList<string>? exports)
    {
        Callable(start, head, nameAt, name, function, frame, marks, decorators, modifiers, subKind, seenIn, exports);
        return function.Last + 1;
    }

    /// <summary>The member node <see cref="Bound"/> makes; answers its key.</summary>
    private string Callable(int start, int head, int nameAt, string name, FunctionShape function, ScanFrame frame, List<string> marks, List<(int Start, int End)> decorators, List<string> modifiers, string subKind, string? seenIn, IReadOnlyList<string>? exports)
    {
        var qualified = frame.Qualify(name);
        var getter = subKind == "getter";
        var prototype = function.Body < 0 || _declarations;
        var parameters = function.Parameters;

        var key = _extract.Add(new ExtractNode(Id(qualified), CodeNodeKinds.Member, name, qualified, _scan[start].Line, _scan[Math.Max(function.Last, start)].EndLine, Signature(head, function.SignatureEnd))
        {
            SubKind = subKind,
            Scope = frame.Scope,
            Parent = frame.Parent,
            BodyStart = function.Body >= 0 ? _scan[function.Body].Line : null,
            Summary = ScanComments.Summary(_scan, start),
            Modifiers = [.. modifiers.Distinct(StringComparer.Ordinal), .. marks],
            MinArity = getter ? null : parameters.Least,
            MaxArity = getter ? null : parameters.Most,
            Prototype = prototype,
            ValueType = getter && function.ReturnStart >= 0 && TypeKey(_scan, function.ReturnStart, function.ReturnEnd) is { } type and not "any" ? type : null,
            SeenIn = seenIn,
            Exports = exports,
        });

        Registered(key, frame, name);
        var own = function.Own.Where(_typeParameters.Add).ToList();

        try
        {
            if (function.ReturnStart >= 0)
            {
                TypeReferences(key, function.ReturnStart, function.ReturnEnd);
            }

            foreach (var (spanStart, spanEnd) in parameters.TypeSpans)
            {
                TypeReferences(key, spanStart, spanEnd);
            }
        }
        finally
        {
            _typeParameters.ExceptWith(own);
        }

        Overriding(key, name, nameAt, modifiers);
        DecoratorCalls(key, decorators, frame.Owner);

        if (function.Body >= 0)
        {
            var typeParameters = _typeParameters.Concat(function.Own).ToList();
            var owner = frame.Owner;

            Defer(() =>
            {
                var added = typeParameters.Where(_typeParameters.Add).ToList();

                try
                {
                    // A function expression's own name is its body's to call itself by: const f = function g() { g(); }.
                    var locals = new Locals(parameters);

                    if (function.Name >= 0 && _scan.Text(function.Name) != name)
                    {
                        locals.LocalFunctions.Add(_scan.Text(function.Name));
                    }

                    Calls(_text, function.Body + 1, function.Last + (function.Arrow ? 1 : 0), key, locals, owner, depth: 0);
                }
                finally
                {
                    _typeParameters.ExceptWith(added);
                }
            });
        }

        return key;
    }

    /// <summary>Reads <paramref name="read"/> once the declarations around it are: the file's every one, or its type's every member.</summary>
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

    /// <summary>The index of the <c>{</c> that opens a type's body from <paramref name="at"/>, past its clauses; -1 where a <c>;</c> or a closing brace comes first.</summary>
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
            else if (_typescript && _scan.Is(at, "<"))
            {
                at = Angle(_scan, at, end) - 1;
            }
        }

        return -1;
    }

    /// <summary>The names of the type parameters the list at <paramref name="less"/> declares.</summary>
    private List<string> TypeParameters(int less, int end)
    {
        var past = Angle(_scan, less, end);

        return [.. ScanCalls.Split(_scan, less + 1, past - 1, _types)
            .Select(piece => _scan.Code(piece.Start, piece.End).FirstOrDefault(index => _scan.IsName(index) && !(_scan.Text(index) is "const" or "in" or "out"), -1))
            .Where(index => index >= 0)
            .Select(index => _scan.Text(index))];
    }

    // ---- Statements' ends ----

    /// <summary>
    /// The index past the statement that begins at <paramref name="at"/>, within <paramref name="end"/>, as JavaScript
    /// ends one: past its <c>;</c> - or its <c>,</c> where <paramref name="commas"/>, as a type's members end - at the
    /// bracket that closes what holds it, or at a line break that ends it, where what is before it may end a statement and
    /// what is after it goes on with none; its groups passed over. Where <paramref name="typed"/> - a type's member, a
    /// field with a type - a list of type arguments is passed over as a group, and a bracket or a parenthesis first on a
    /// line begins another member, as TypeScript reads a type: no type goes on with one after a line break. Past a
    /// member's <c>=</c> neither holds: its value is code.
    /// </summary>
    private int StatementEnd(int at, int end, bool commas = false, bool typed = false)
    {
        var stop = Math.Min(end, _scan.Count);

        for (var index = at; index < stop;)
        {
            if (_scan.Is(index, ";") || (commas && _scan.Is(index, ",")))
            {
                return index + 1;
            }

            // A typed member's value is code, as any statement's, a comma in it no member's end: private ok: boolean = this.n < 3,
            // [KEY] = new Map<string, number>().
            typed &= !_scan.Is(index, "=");
            commas &= !_scan.Is(index, "=");

            if (_scan.Is(index, "}") || _scan.Is(index, ")") || _scan.Is(index, "]"))
            {
                return Math.Max(index, at + 1);
            }

            var past = _scan.Opens(index) ? Math.Min(_scan.Bounds(index, stop).Past, stop)
                : typed && _scan.Is(index, "<") ? Math.Min(Angle(_scan, index, stop), stop)
                : index + 1;

            if (past >= stop)
            {
                return stop;
            }

            if (_scan[past].Line > _scan[past - 1].EndLine && EndsStatement(past - 1) && (!GoesOnAt(past) || (typed && (_scan.Is(past, "[") || _scan.Is(past, "(") || _scan.Is(past, "<")))))
            {
                return past;
            }

            index = past;
        }

        return stop;
    }

    /// <summary>
    /// Whether a statement may end at the token at <paramref name="at"/>: a name but a word an expression goes on after - a
    /// member's name whatever word it spells, TypeScript's <c>void</c>, which ends a type, and <c>as const</c> - a number, a
    /// text, a group's closer, a postfix <c>++</c> or <c>--</c>, a closed list of type arguments.
    /// </summary>
    private bool EndsStatement(int at)
    {
        var token = _scan[at];

        return token.Kind switch
        {
            ScanTokenKind.Name => !GoesOn.Contains(_scan.Text(at)) || IsMember(_scan, _scan.Previous(at)) || (_typescript && _scan.Is(at, "void"))
                || (_scan.Is(at, "const") && _scan.Is(_scan.Previous(at), "as")),
            ScanTokenKind.Number or ScanTokenKind.Text => true,
            _ => _scan.Is(at, ")") || _scan.Is(at, "]") || _scan.Is(at, "}") || _scan.Is(at, "!")
                || ((_scan.Is(at, "+") || _scan.Is(at, "-")) && _scan.Is(_scan.Previous(at), _scan.Text(at)) && _scan[_scan.Previous(at)].End == token.Start)
                || (_types is not null && _scan.Is(at, ">") && ScanCalls.TypeArgumentsStart(_scan, at, _types) is var less and >= 0 && _scan.IsName(_scan.Previous(less))),
        };
    }

    /// <summary>Whether the token at <paramref name="at"/>, first on its line, goes on with the expression before it: an operator, a member's dot, an argument list, a word such as <c>instanceof</c>.</summary>
    private bool GoesOnAt(int at)
    {
        var token = _scan[at];

        if (token.Kind == ScanTokenKind.Name)
        {
            return Continuing.Contains(_scan.Text(at));
        }

        if (token.Kind != ScanTokenKind.Punctuation)
        {
            return false;
        }

        if (_scan.Is(at, "{"))
        {
            return _scan.Is(_scan.Previous(at), ")") || _scan.Is(_scan.Previous(at), "=>");
        }

        return !(_scan.Is(at, "!") || _scan.Is(at, "~") || _scan.Is(at, "@") || _scan.Is(at, "#") || _scan.Is(at, ";") || _scan.Is(at, "}")
            || ((_scan.Is(at, "+") || _scan.Is(at, "-")) && _scan.Is(_scan.Next(at), _scan.Text(at))));
    }

    /// <summary>
    /// The index past the type that begins at <paramref name="at"/>, within <paramref name="end"/>: names and their
    /// chains, type arguments, unions and intersections, arrays, tuples and object types, function types and their
    /// arrows, conditional types, predicates, literal types - stopping where a type is complete and what follows goes on
    /// with none, as a body's brace or an arrow function's arrow.
    /// </summary>
    private static int TypeEnd(ScannedText scan, int at, int end)
    {
        var expected = true;
        var conditions = 0;

        while (at < end && at < scan.Count)
        {
            if (expected)
            {
                if (scan.Is(at, "(") || scan.Is(at, "{") || scan.Is(at, "["))
                {
                    var past = Math.Min(scan.Past(at), end);

                    // A function type's parameters: its arrow, then its return type.
                    if (scan.Is(at, "(") && scan.Is(past, "=>"))
                    {
                        at = scan.Next(past);
                        continue;
                    }

                    at = past;
                    expected = false;
                }
                else if (scan.Is(at, "<"))
                {
                    at = Angle(scan, at, end);
                }
                else if (scan.IsName(at) && scan.Text(at) is "typeof" or "keyof" or "readonly" or "unique" or "infer" or "new" or "abstract" or "asserts")
                {
                    at = scan.Next(at);
                }
                else if (scan.Is(at, "import") && scan.Is(scan.Next(at), "("))
                {
                    // typeof import('./x')
                    at = Math.Min(scan.Past(scan.Next(at)), end);
                    expected = false;
                }
                else if (scan.IsName(at) || scan[at].Kind is ScanTokenKind.Number or ScanTokenKind.Text)
                {
                    at = scan.Next(at);
                    expected = false;
                }
                else if (scan.Is(at, "|") || scan.Is(at, "&") || scan.Is(at, "-") || scan.Is(at, "..."))
                {
                    at = scan.Next(at);
                }
                else
                {
                    return at;
                }
            }
            else if (scan.Is(at, "<"))
            {
                at = Angle(scan, at, end);
            }
            else if (scan.Is(at, "["))
            {
                at = Math.Min(scan.Past(at), end);
            }
            else if (scan.Is(at, "|") || scan.Is(at, "&") || scan.Is(at, "."))
            {
                at = scan.Next(at);
                expected = true;
            }
            else if (scan.Is(at, "extends") || scan.Is(at, "is"))
            {
                at = scan.Next(at);
                expected = true;
            }
            else if (scan.Is(at, "?") && IsConditional(scan, at))
            {
                conditions++;
                at = scan.Next(at);
                expected = true;
            }
            else if (scan.Is(at, ":") && conditions > 0)
            {
                conditions--;
                at = scan.Next(at);
                expected = true;
            }
            else
            {
                return at;
            }
        }

        return Math.Min(end, scan.Count);
    }

    /// <summary>
    /// The index past the <c>&gt;</c> that closes the <c>&lt;</c> at <paramref name="less"/>, within <paramref name="end"/>:
    /// a list of type arguments or parameters, every group within passed over - an object type's braces among them,
    /// <c>&lt;T extends object = {}&gt;</c> - and cut short at a <c>;</c>; always past <paramref name="less"/>.
    /// </summary>
    private static int Angle(ScannedText scan, int less, int end)
    {
        var depth = 0;

        for (var at = less; at < end && at < scan.Count; at++)
        {
            if (scan.Is(at, "<"))
            {
                depth++;
            }
            else if (scan.Is(at, ">") && --depth == 0)
            {
                return at + 1;
            }
            else if (scan.Opens(at))
            {
                at = Math.Max(at, Math.Min(scan.Past(at), end) - 1);
            }
            else if (scan.Is(at, ";") || scan.Is(at, "}") || scan.Is(at, ")") || scan.Is(at, "]"))
            {
                return Math.Max(at, less + 1);
            }
        }

        return Math.Max(Math.Min(end, scan.Count), less + 1);
    }

    /// <summary>Whether the <c>?</c> at <paramref name="at"/> is a conditional type's: an <c>extends</c> stands before it, past what it tests.</summary>
    private static bool IsConditional(ScannedText scan, int at)
    {
        for (var index = scan.Previous(at); index >= 0; index = scan.Previous(index))
        {
            if (scan.Is(index, "extends"))
            {
                return true;
            }

            if (scan.Is(index, ")") || scan.Is(index, "]") || scan.Is(index, "}"))
            {
                if (scan.Match(index) is not (var open and >= 0))
                {
                    return false;
                }

                index = open;
            }
            else if (scan.Opens(index) || scan.Is(index, ";") || scan.Is(index, ",") || scan.Is(index, "=") || scan.Is(index, ":"))
            {
                return false;
            }
        }

        return false;
    }

    // ---- Parameters ----

    /// <summary>A parameter property's tokens: where it begins and ends, its name, its type's span, and its modifiers.</summary>
    private sealed record ParameterProperty(int Start, int End, int NameAt, int TypeStart, int TypeEnd, List<string> Modifiers);

    /// <summary>
    /// A callable's parameters: how many a call gives - in TypeScript its required ones the fewest and all of them the
    /// most, none where a rest one takes any; in JavaScript any number - the spans of their types, the names each binds
    /// with the type it is declared with, those of a function type, and the parameter properties a constructor declares.
    /// </summary>
    private sealed record Parameters(int? Least, int? Most, IReadOnlyList<(int Start, int End)> TypeSpans, IReadOnlyDictionary<string, string?> Locals, IReadOnlySet<string> Functions, IReadOnlyList<ParameterProperty> Properties);

    /// <summary>The parameters between the parentheses at <paramref name="open"/> and <paramref name="close"/>.</summary>
    private Parameters ParametersOf(ScannedText scan, int open, int close)
    {
        var least = 0;
        var most = 0;
        var rest = false;
        var spans = new List<(int, int)>();
        var locals = new Dictionary<string, string?>(StringComparer.Ordinal);
        var functions = new HashSet<string>(StringComparer.Ordinal);
        var properties = new List<ParameterProperty>();

        foreach (var (start, end) in ScanCalls.Split(scan, open + 1, close, _types))
        {
            var at = SkipDecorators(scan, start, end);
            var modifiers = new List<string>();

            while (scan.IsName(at) && ParameterModifiers.Contains(scan.Text(at)) && scan.Next(at) < end && (scan.IsName(scan.Next(at)) || scan.Opens(scan.Next(at))))
            {
                modifiers.Add(scan.Text(at));
                at = scan.Next(at);
            }

            // TypeScript's this parameter says what the function is called on, and takes no argument.
            if (scan.Is(at, "this") && scan.Is(scan.Next(at), ":"))
            {
                continue;
            }

            if (scan.Is(at, "..."))
            {
                rest = true;
                at = scan.Next(at);
            }

            var nameAt = scan.IsName(at) ? at : -1;
            var next = nameAt >= 0 ? scan.Next(at) : scan.Opens(at) ? Math.Min(scan.Past(at), end) : end;

            if (nameAt < 0 && scan.Opens(at))
            {
                foreach (var bound in PatternNames(scan, at))
                {
                    locals[bound] = null;
                }
            }

            var optional = false;

            if (scan.Is(next, "?"))
            {
                optional = true;
                next = scan.Next(next);
            }

            var typeStart = _typescript && scan.Is(next, ":") && next < end ? scan.Next(next) : -1;
            var assign = ScanCalls.TopLevel(scan, typeStart >= 0 ? typeStart : next, end, "=", _types);
            var typeEnd = typeStart >= 0 ? assign : -1;
            optional |= assign < end;

            if (typeStart >= 0)
            {
                spans.Add((typeStart, typeEnd));
            }

            if (nameAt >= 0)
            {
                var name = scan.Text(nameAt);
                locals[name] = typeStart >= 0 ? TypeKey(scan, typeStart, typeEnd) : null;

                if (typeStart >= 0 && IsFunctionType(scan, typeStart, typeEnd))
                {
                    functions.Add(name);
                }

                if (modifiers.Count > 0)
                {
                    properties.Add(new ParameterProperty(start, end, nameAt, typeStart, typeEnd, modifiers));
                }
            }

            if (!rest)
            {
                most++;
                least += optional ? 0 : 1;
            }
        }

        return _typescript
            ? new Parameters(least, rest ? null : most, spans, locals, functions, properties)
            : new Parameters(0, null, spans, locals, functions, properties);
    }

    /// <summary>Past the decorators of a parameter at <paramref name="at"/>, within <paramref name="end"/>.</summary>
    private static int SkipDecorators(ScannedText scan, int at, int end)
    {
        while (at < end && scan.Is(at, "@") && scan.IsName(scan.Next(at)))
        {
            var (_, past) = ChainAt(scan, scan.Next(at), end);
            at = scan.Is(past, "(") ? Math.Min(scan.Past(past), end) : past;
        }

        return at;
    }

    /// <summary>The names the pattern the bracket at <paramref name="open"/> opens binds: each name a comma, a closer or a default follows, and no key's.</summary>
    private static List<string> PatternNames(ScannedText scan, int open)
    {
        var names = new List<string>();
        var close = scan.Match(open) is var matched and > 0 ? matched : open;

        for (var at = open + 1; at < close; at++)
        {
            var next = scan.Next(at);

            if (scan.IsName(at) && !IsMember(scan, scan.Previous(at)) && (scan.Is(next, ",") || scan.Is(next, "}") || scan.Is(next, "]") || scan.Is(next, "=")))
            {
                names.Add(scan.Text(at));
            }
        }

        return names;
    }

    // ---- What a body calls ----

    /// <summary>The names a body binds: each local's and parameter's type where the code says one, those of a function type, and its local functions.</summary>
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
    /// node of <paramref name="key"/> - the module's, made where it is first needed, where none is given: a reference by
    /// its name and arity, qualified by its receiver where the code says - a member's, <c>this</c>, <c>super</c>, an
    /// import's prefix - <c>new</c> an <c>instantiates</c> one, a JSX element's component a call, and what it cannot bind
    /// marked dynamic. A template's and an element's code are read as text of their own, an import a call makes an import,
    /// and a test file's test calls nodes of their own. <paramref name="owner"/> is the type whose members the body sees.
    /// </summary>
    private void Calls(InterpolatedText text, int from, int to, string? key, Locals locals, string? owner, int depth)
    {
        var scan = text.Scan;
        string From() => key ?? ModuleKey();

        // A function a body declares is its own from the body's first line: JavaScript hoists it.
        if (key is not null)
        {
            Hoisted(scan, from, to, locals);
        }

        // Where the test calls read are no longer too deep to be: past the first that was.
        var shallowFrom = -1;

        for (var at = from; at < to && at < scan.Count; at++)
        {
            var token = scan[at];

            if (token.Kind == ScanTokenKind.Text)
            {
                if (text.Interpolations.TryGetValue(token.Start, out var spans) && Deeper(depth, token.Line))
                {
                    foreach (var (spanStart, spanEnd) in spans)
                    {
                        var inner = JsLexer.Scan(scan.Source, spanStart, spanEnd, text.Lines, _markup);
                        Calls(inner, 0, inner.Scan.Count, key, locals, owner, depth + 1);
                    }
                }

                if (text.Elements is { } elements && elements.TryGetValue(token.Start, out var tags))
                {
                    foreach (var (tagStart, tagEnd) in tags)
                    {
                        Component(scan.Source[tagStart..tagEnd], text.Lines.LineAt(tagStart), From, locals);
                    }
                }

                continue;
            }

            if (token.Kind != ScanTokenKind.Name)
            {
                if (scan.Is(at, "(") && scan.Match(at) is var close and > 0 && IsArrowParameters(scan, close))
                {
                    locals.Add(ParametersOf(scan, at, close));
                }
                else if (scan.Is(at, "[") && scan.Match(at) is var closing and > 0 && scan.Is(scan.Next(closing), "(") && EndsOperand(scan, scan.Previous(at)))
                {
                    _extract.Dynamic(token.Line, "a call through a computed member");
                }

                continue;
            }

            var word = scan.Text(at);
            var line = token.Line;
            var next = scan.Next(at);
            var previous = scan.Previous(at);
            var member = IsMember(scan, previous);

            if (!member)
            {
                switch (word)
                {
                    case "function":
                        if (FunctionLocals(scan, at, locals) is var past and > 0)
                        {
                            at = past - 1;
                        }

                        continue;

                    case "catch" when scan.Is(next, "(") && scan.Match(next) is var caught and > 0:
                        foreach (var bound in scan.Code(next + 1, caught).Where(index => scan.IsName(index) && !scan.Is(scan.Previous(index), ":")).Take(1))
                        {
                            locals.Types[scan.Text(bound)] = null;
                        }

                        at = caught;
                        continue;

                    case "new":
                        if (Instantiated(scan, at, From, locals) is var made and > 0)
                        {
                            at = made - 1;
                        }

                        continue;

                    case "super" when scan.Is(next, "("):
                        _extract.Reference(From(), CodeEdgeKinds.Calls, "constructor", line, ScanCalls.CallArity(scan, next, CloseOf(scan, next), _calls), new ExtractQualifier(CodeQualifierKinds.Base));
                        continue;

                    // import('./x'), require('./x'): the file imported, no name of it bound; one the code computes, marked dynamic.
                    case "import" or "require" when scan.Is(next, "(") && !locals.Types.ContainsKey(word) && !_bindings.ContainsKey(word):
                        if (WholeSpecifier(scan, next) is not { } specifier)
                        {
                            _extract.Dynamic(line, $"an {(word == "import" ? "import" : "import by require")} of a module the code computes");
                        }
                        else if (_required.Add(specifier))
                        {
                            Run(specifier, line, null, [], export: false);
                        }

                        continue;

                    case "eval" when scan.Is(next, "("):
                        _extract.Dynamic(line, "a call of eval");
                        continue;

                    case "as" or "satisfies" when _typescript && !scan.Is(next, "const"):
                        var typed = TypeEnd(scan, next, to);

                        if (scan == _scan)
                        {
                            TypeReferences(From(), next, typed);
                        }

                        at = Math.Max(at, typed - 1);
                        continue;

                    case "const" or "let" or "var" when scan.Opens(next):
                        foreach (var bound in PatternNames(scan, next))
                        {
                            locals.Types[bound] = null;
                        }

                        continue;
                }

                Local(scan, at, to, locals);

                if (scan.Is(next, "=>"))
                {
                    locals.Types[word] = null;
                    continue;
                }

                // A test call nested too deep is marked once, those it holds read as its holder's calls.
                if (_testFile && (TestGroups.Contains(word) || TestCases.Contains(word)) && at > shallowFrom && !locals.Types.ContainsKey(word))
                {
                    if (TestCall(scan, at) is { } test)
                    {
                        if (!Deeper(depth, line))
                        {
                            shallowFrom = test.Close;
                        }
                        else if (Test(text, at, test, key, locals, owner, depth) is var tested and > 0)
                        {
                            at = tested;
                            continue;
                        }
                    }
                }
            }

            // A tagged template: a call of what stands before the template.
            if (IsTemplate(scan, next) && !NoCalls.Contains(word))
            {
                if (member)
                {
                    MemberCall(scan, previous, null, From(), locals, word, line, owner);
                }
                else if (!locals.Types.ContainsKey(word) && !locals.LocalFunctions.Contains(word))
                {
                    var (tagged, qualifier) = Callee(word);
                    _extract.Reference(From(), CodeEdgeKinds.Calls, tagged, line, qualifier: qualifier);
                }

                continue;
            }

            if ((ScanCalls.At(scan, at, member ? _memberCalls : _calls) ?? Optional(scan, at)) is not { } call)
            {
                continue;
            }

            // A method's definition - an object's, a class expression's - is no call: its parameters are locals.
            if (IsDefinition(scan, call))
            {
                locals.Add(ParametersOf(scan, call.Open, call.Close));
                at = call.Close;
                continue;
            }

            if (member)
            {
                MemberCall(scan, previous, call, From(), locals, word, line, owner);
                continue;
            }

            if (locals.LocalFunctions.Contains(word))
            {
                continue;
            }

            if (locals.Types.ContainsKey(word) || locals.Functions.Contains(word) || _values.Contains(word))
            {
                _extract.Dynamic(line, $"a call through the function value {word}");
                continue;
            }

            var (name, qualified) = Callee(word);
            _extract.Reference(From(), CodeEdgeKinds.Calls, name, line, call.Arity, qualified);
        }
    }

    /// <summary>
    /// Where the name a <c>function</c> at <paramref name="at"/> is written with stands - past a generator's <c>*</c> - or
    /// what stands there where it has none, and whether it is a generator's.
    /// </summary>
    private static (int Name, bool Generator) FunctionName(ScannedText scan, int at)
    {
        var next = scan.Next(at);
        return scan.Is(next, "*") ? (scan.Next(next), true) : (next, false);
    }

    /// <summary>The names of the functions the tokens from <paramref name="from"/> to <paramref name="to"/> declare - a <c>function</c> where a statement begins - each a local function of <paramref name="locals"/>.</summary>
    private static void Hoisted(ScannedText scan, int from, int to, Locals locals)
    {
        for (var at = from; at < to && at < scan.Count; at++)
        {
            if (!scan.Is(at, "function"))
            {
                continue;
            }

            var before = scan.Previous(at);

            if (before >= from && !(scan.Is(before, "{") || scan.Is(before, "}") || scan.Is(before, ";") || scan.Is(before, "async") || scan.Is(before, ")")))
            {
                continue;
            }

            if (FunctionName(scan, at).Name is var name && scan.IsName(name))
            {
                locals.LocalFunctions.Add(scan.Text(name));
            }
        }
    }

    /// <summary>
    /// A <c>function</c> expression or declaration at <paramref name="at"/> in a body: its parameters its body's locals,
    /// its name a local function, whose calls are what the body around it calls; answers the index of its parameters, or
    /// 0 where none follow.
    /// </summary>
    private int FunctionLocals(ScannedText scan, int at, Locals locals)
    {
        var next = FunctionName(scan, at).Name;

        if (scan.IsName(next))
        {
            locals.LocalFunctions.Add(scan.Text(next));
            next = scan.Next(next);
        }

        if (_typescript && scan.Is(next, "<"))
        {
            next = Angle(scan, next, scan.Count);
        }

        if (!scan.Is(next, "(") || scan.Match(next) is not (var close and > 0))
        {
            return 0;
        }

        locals.Add(ParametersOf(scan, next, close));
        return close + 1;
    }

    /// <summary>An optional call of the name at <paramref name="at"/> - <c>a?.(...)</c> - where one is written; <see langword="null"/> otherwise.</summary>
    private ScannedCall? Optional(ScannedText scan, int at)
    {
        var dot = scan.Next(at);
        var open = scan.Next(dot);

        if (!scan.Is(dot, "?.") || !scan.Is(open, "(") || NoCalls.Contains(scan.Text(at)))
        {
            return null;
        }

        var close = CloseOf(scan, open);
        return new ScannedCall(at, open, close, ScanCalls.CallArity(scan, open, close, _calls));
    }

    /// <summary>Whether <paramref name="call"/> is a method's definition: a body follows its parameters on their line, past a return type.</summary>
    private bool IsDefinition(ScannedText scan, ScannedCall call)
    {
        if (call.Close >= scan.Count)
        {
            return false;
        }

        var after = scan.Next(call.Close);

        if (_typescript && scan.Is(after, ":"))
        {
            after = TypeEnd(scan, scan.Next(after), scan.Count);
        }

        return scan.Is(after, "{") && scan[after].Line == scan[call.Close].EndLine;
    }

    /// <summary>Whether the parenthesis closed at <paramref name="close"/> holds an arrow function's parameters: its arrow follows, past a return type.</summary>
    private bool IsArrowParameters(ScannedText scan, int close)
    {
        var after = scan.Next(close);

        if (_typescript && scan.Is(after, ":"))
        {
            after = TypeEnd(scan, scan.Next(after), scan.Count);
        }

        return scan.Is(after, "=>");
    }

    /// <summary>Whether an operand ends at <paramref name="at"/>, so a <c>[</c> after it is a member's: a name but a keyword an expression begins after, a literal, a group's closer.</summary>
    private static bool EndsOperand(ScannedText scan, int at)
        => at >= 0 && at < scan.Count
            && ((scan[at].Kind == ScanTokenKind.Name && !(scan.Text(at) is "return" or "typeof" or "in" or "of" or "new" or "delete" or "void" or "throw" or "case" or "yield" or "await" or "else"))
                || scan[at].Kind == ScanTokenKind.Text
                || scan.Is(at, ")") || scan.Is(at, "]") || scan.Is(at, "?."));

    /// <summary>
    /// A local the body declares at <paramref name="at"/>, where it declares one after <c>const</c>, <c>let</c> or
    /// <c>var</c>: of the type its annotation names, or its value makes; a local function where a function or an arrow is
    /// its value.
    /// </summary>
    private void Local(ScannedText scan, int at, int to, Locals locals)
    {
        var previous = scan.Previous(at);

        if (!(scan.Is(previous, "const") || scan.Is(previous, "let") || scan.Is(previous, "var")) || IsMember(scan, scan.Previous(previous)))
        {
            return;
        }

        var word = scan.Text(at);
        var next = scan.Next(at);

        if (_typescript && scan.Is(next, ":"))
        {
            var typeEnd = TypeEnd(scan, scan.Next(next), to);
            locals.Types[word] = TypeKey(scan, scan.Next(next), typeEnd);

            if (IsFunctionType(scan, scan.Next(next), typeEnd))
            {
                locals.Functions.Add(word);
            }

            return;
        }

        if (scan.Is(next, "="))
        {
            var value = scan.Next(next);

            if (scan == _scan && FunctionAt(value, Math.Min(to, scan.Count)) is not null)
            {
                locals.LocalFunctions.Add(word);
                return;
            }

            locals.Types[word] = Made(scan, value, Math.Min(to, scan.Count));
            return;
        }

        locals.Types[word] = null;
    }

    /// <summary>
    /// A call of <paramref name="word"/> on what stands before the <c>.</c> or <c>?.</c> at <paramref name="dot"/>: a
    /// reference qualified by that receiver - none of an arity where <paramref name="call"/> is a tagged template's; one
    /// through a function value's <c>call</c> or <c>apply</c>, <c>Reflect</c>'s, or a field of a function type marked
    /// dynamic, and not linked.
    /// </summary>
    private void MemberCall(ScannedText scan, int dot, ScannedCall? call, string key, Locals locals, string word, int line, string? owner)
    {
        var before = scan.Previous(dot);

        if (word is "apply" or "construct" && scan.Is(before, "Reflect") && !IsMember(scan, scan.Previous(before)))
        {
            _extract.Dynamic(line, $"a call through Reflect.{word}");
            return;
        }

        var receiver = Receiver(scan, dot, locals, out var anyValue);

        // A function value's call or apply - f.call(this, x) - and not a method of that name a type the file says has.
        if (word is "call" or "apply" && receiver.Kind is CodeQualifierKinds.Value && anyValue is null)
        {
            _extract.Dynamic(line, $"a call through a function value's {word}");
            return;
        }

        if (anyValue is not null)
        {
            _extract.Dynamic(line, $"a call on the any value {anyValue}");
        }

        if (receiver.Kind == CodeQualifierKinds.This && owner is not null && _functionFields.TryGetValue(owner, out var fields) && fields.Contains(word))
        {
            _extract.Dynamic(line, $"a call through the function value {word}");
            return;
        }

        _extract.Reference(key, CodeEdgeKinds.Calls, word, line, call?.Arity, receiver);
    }

    /// <summary>
    /// What a call on the receiver before the token at <paramref name="dot"/> is qualified by: <c>this</c>, <c>super</c>, the
    /// type a local is declared with or made of, an import's prefix, a type's name, a value by its name, or a type a
    /// <c>new</c> makes - <paramref name="anyValue"/> the local's name where it is typed <c>any</c>.
    /// </summary>
    private ExtractQualifier Receiver(ScannedText scan, int dot, Locals locals, out string? anyValue)
    {
        anyValue = null;
        var names = new List<string>();
        var at = Unasserted(scan, scan.Previous(dot));
        var first = -1;

        while (scan.IsName(at))
        {
            names.Insert(0, scan.Text(at));
            first = at;
            var before = scan.Previous(at);

            if (!IsMember(scan, before))
            {
                break;
            }

            at = Unasserted(scan, scan.Previous(before));
        }

        if (names.Count == 0)
        {
            var close = Unasserted(scan, scan.Previous(dot));

            if (scan.Is(close, ")") && scan.Match(close) is var cast and >= 0 && CastToAny(scan, cast, close))
            {
                anyValue = scan.Spelled(scan.Next(cast), close);
                return new ExtractQualifier(CodeQualifierKinds.Value);
            }

            return scan.Is(close, ")") && scan.Match(close) is var open and >= 0 && Made(scan, open) is { } made
                ? new ExtractQualifier(CodeQualifierKinds.Type, made)
                : new ExtractQualifier(CodeQualifierKinds.Value);
        }

        // A chain that is a member of something more - a call's result, an element's - is a value of no type the file says.
        if (IsMember(scan, scan.Previous(first)))
        {
            return new ExtractQualifier(CodeQualifierKinds.Value);
        }

        return ChainQualifier(names, locals, out anyValue);
    }

    /// <summary>Whether the parentheses at <paramref name="open"/> and <paramref name="close"/> cast what they hold to <c>any</c>: <c>(x as any)</c>, <c>(&lt;any&gt;x)</c>.</summary>
    private static bool CastToAny(ScannedText scan, int open, int close)
        => (scan.Is(scan.Previous(close), "any") && scan.Is(scan.Previous(scan.Previous(close)), "as"))
            || (scan.Is(scan.Next(open), "<") && scan.Is(scan.Next(scan.Next(open)), "any") && scan.Is(scan.Next(scan.Next(scan.Next(open))), ">"));

    /// <summary>What the chain of names <paramref name="names"/> a member is written after qualifies it by (<see cref="Receiver"/>).</summary>
    private ExtractQualifier ChainQualifier(List<string> names, Locals? locals, out string? anyValue)
    {
        anyValue = null;

        if (names[0] == "this")
        {
            return names.Count == 1 ? new ExtractQualifier(CodeQualifierKinds.This) : new ExtractQualifier(CodeQualifierKinds.Value, string.Join('.', names.Skip(1)));
        }

        if (names[0] == "super")
        {
            return names.Count == 1 ? new ExtractQualifier(CodeQualifierKinds.Base) : new ExtractQualifier(CodeQualifierKinds.Value);
        }

        if (locals is not null && locals.Types.TryGetValue(names[0], out var declared))
        {
            if (declared == "any")
            {
                anyValue = names[0];
                return new ExtractQualifier(CodeQualifierKinds.Value);
            }

            return names.Count == 1 && declared is not null ? new ExtractQualifier(CodeQualifierKinds.Type, declared) : new ExtractQualifier(CodeQualifierKinds.Value);
        }

        var expanded = Expanded(names);

        if (_bindings.TryGetValue(names[0], out var binding) && binding.Prefixed)
        {
            return expanded.Count == 1 ? new ExtractQualifier(CodeQualifierKinds.Prefix, names[0]) : new ExtractQualifier(CodeQualifierKinds.Named, string.Join('.', expanded));
        }

        return IsTypeName(names[0]) ? new ExtractQualifier(CodeQualifierKinds.Named, string.Join('.', expanded)) : new ExtractQualifier(CodeQualifierKinds.Value, string.Join('.', expanded));
    }

    /// <summary>
    /// <paramref name="names"/>, a chain written in the file, as the linker reads it: one that begins with a name a default
    /// or a renamed import binds has the name the imported file exports it by after that name, the import's prefix.
    /// </summary>
    private List<string> Expanded(IReadOnlyList<string> names)
        => _bindings.TryGetValue(names[0], out var binding) && binding is { Prefixed: true, Export: { } export } ? [names[0], export, .. names.Skip(1)] : [.. names];

    /// <summary>What a call of <paramref name="word"/> written alone names, and what qualifies it: the export a default, renamed or namespace import binds, by its prefix; itself otherwise.</summary>
    private (string Name, ExtractQualifier? Qualifier) Callee(string word)
        => _bindings.TryGetValue(word, out var binding) && binding.Prefixed
            ? (binding.Export ?? "default", new ExtractQualifier(CodeQualifierKinds.Prefix, word))
            : (word, null);

    /// <summary>
    /// A JSX element's tag named <paramref name="tag"/>, on <paramref name="line"/>: a component - a capitalized name, or a
    /// member's chain - a call of it from <paramref name="from"/>'s node; one a local holds marked dynamic; an element of
    /// markup's own, none.
    /// </summary>
    private void Component(string tag, int line, Func<string> from, Locals locals)
    {
        var names = tag.Split('.');

        if (names.Any(name => name.Length == 0 || !name.All(ScanLexing.IsNamePart)) || (names.Length == 1 && !char.IsUpper(names[0][0])))
        {
            return;
        }

        if (names.Length == 1 && locals.LocalFunctions.Contains(names[0]))
        {
            return;
        }

        if (locals.Types.ContainsKey(names[0]) || locals.Functions.Contains(names[0]))
        {
            _extract.Dynamic(line, $"a component held by the value {names[0]}");
            return;
        }

        if (names.Length == 1)
        {
            var (name, qualifier) = Callee(names[0]);
            _extract.Reference(from(), CodeEdgeKinds.Calls, name, line, qualifier: qualifier);
            return;
        }

        _extract.Reference(from(), CodeEdgeKinds.Calls, names[^1], line, qualifier: ChainQualifier([.. names[..^1]], locals, out _));
    }

    /// <summary>
    /// A <c>new</c> at <paramref name="at"/> before a type's name: an <c>instantiates</c> reference to it - <c>new Function</c>
    /// marked dynamic; answers where the reading goes on - at its arguments - or 0 where what follows is no name.
    /// </summary>
    private int Instantiated(ScannedText scan, int at, Func<string> from, Locals locals)
    {
        var (names, past) = ChainAt(scan, scan.Next(at), scan.Count);

        if (names.Count == 0 || (NoCalls.Contains(scan.Text(names[0])) && !scan.Is(names[0], "this")))
        {
            return 0;
        }

        var written = names.Select(index => scan.Text(index)).ToList();

        // new this.X(), and a function the body declares: none of the repository's.
        if (written[0] == "this" || (written.Count == 1 && locals.LocalFunctions.Contains(written[0])))
        {
            return past;
        }

        if (written is ["Function"])
        {
            _extract.Dynamic(scan[at].Line, "a function made by new Function");
            return past;
        }

        // Map, Date, Error and the rest of the language's own: no type the repository declares.
        if (written.Count > 1 || !Builtins.Contains(written[0]))
        {
            Mention(from(), CodeEdgeKinds.Instantiates, string.Join('.', written), scan[names[0]].Line);
        }

        if (_types is not null && scan.Is(past, "<") && ScanCalls.TypeArgumentsEnd(scan, past, _types) is var arguments and > 0)
        {
            if (scan == _scan)
            {
                TypeReferences(from(), past + 1, arguments - 1);
            }

            past = arguments;
        }

        return Math.Max(past, at + 1);
    }

    /// <summary>
    /// The type a <c>new</c> makes whose arguments the parenthesis at <paramref name="open"/> opens, where one does - its
    /// name, an import's prefix before it - as the linker reads it; <see langword="null"/> where no <c>new</c> stands before.
    /// </summary>
    private string? Made(ScannedText scan, int open)
    {
        var at = scan.Previous(open);

        if (_types is not null && scan.Is(at, ">") && ScanCalls.TypeArgumentsStart(scan, at, _types) is var less and >= 0)
        {
            at = scan.Previous(less);
        }

        if (!scan.IsName(at))
        {
            return null;
        }

        var first = FirstOfChain(scan, at);

        if (!scan.Is(scan.Previous(first), "new"))
        {
            return null;
        }

        var written = ChainAt(scan, first, scan.Next(at)).Names.Select(index => scan.Text(index)).ToList();
        return written[0] is "this" or "Function" ? null : string.Join('.', Expanded(written));
    }

    /// <summary>The type the value from <paramref name="at"/> to <paramref name="end"/> makes, where it is a <c>new</c> of one and nothing more; <see langword="null"/> otherwise.</summary>
    private string? Made(ScannedText scan, int at, int end)
    {
        if (!scan.Is(at, "new"))
        {
            return null;
        }

        var (names, past) = ChainAt(scan, scan.Next(at), end);

        if (names.Count == 0)
        {
            return null;
        }

        if (_types is not null && scan.Is(past, "<") && ScanCalls.TypeArgumentsEnd(scan, past, _types) is var arguments and > 0)
        {
            past = arguments;
        }

        if (scan.Is(past, "("))
        {
            past = scan.Past(past);
        }

        if (!(past >= end || past >= scan.Count || scan.Is(past, ";") || scan.Is(past, ",") || scan[past].Line > scan[scan.Previous(past)].EndLine))
        {
            return null;
        }

        var written = names.Select(index => scan.Text(index)).ToList();
        return written[0] is "this" or "Function" ? null : string.Join('.', Expanded(written));
    }

    /// <summary>A test call's shape: where its description's arguments open and close, and whether it is a group's.</summary>
    private readonly record struct TestShape(int Open, int Close, bool Group, string Written);

    /// <summary>
    /// The test call whose first name is at <paramref name="at"/> - <c>describe</c>, <c>it</c>, <c>test</c> and the rest,
    /// with <c>.only</c>, <c>.skip</c>, <c>.each(table)</c> and the rest - where it gives its description in parentheses;
    /// <see langword="null"/> where it does not.
    /// </summary>
    private static TestShape? TestCall(ScannedText scan, int at)
    {
        var word = scan.Text(at);
        var group = TestGroups.Contains(word);
        var written = word;
        var next = scan.Next(at);

        while (scan.Is(next, ".") && scan.IsName(scan.Next(next)) && TestVariants.Contains(scan.Text(scan.Next(next))))
        {
            var variant = scan.Text(scan.Next(next));
            group |= variant == "describe";
            written += "." + variant;
            next = scan.Next(scan.Next(next));

            if (variant == "each")
            {
                if (scan.Is(next, "("))
                {
                    next = scan.Past(next);
                }
                else if (IsTemplate(scan, next))
                {
                    next = scan.Next(next);
                }
            }
        }

        if (!scan.Is(next, "(") || scan.Match(next) is not (var close and > 0))
        {
            return null;
        }

        return new TestShape(next, close, group, written);
    }

    /// <summary>
    /// A test framework's call at <paramref name="at"/> as a node of its own, named by its description and held by the node
    /// of <paramref name="parent"/> - none at a module's top - what its arguments call its own; answers the index of its
    /// closing parenthesis, or 0 where it gives no description.
    /// </summary>
    private int Test(InterpolatedText text, int at, TestShape test, string? parent, Locals locals, string? owner, int depth)
    {
        var scan = text.Scan;
        var pieces = ScanCalls.Split(scan, test.Open + 1, test.Close, _types);

        if (pieces.Count == 0 || test.Close >= scan.Count || ExtractText.Flat(scan.Spelled(pieces[0].Start, pieces[0].End)) is not { Length: > 0 } name)
        {
            return 0;
        }

        var qualified = parent is null ? ExtractText.Qualify(_scope, name) : $"{_extract.Node(parent).Qualified}.{name}";
        var key = _extract.Add(new ExtractNode(Id(qualified), CodeNodeKinds.Member, name, qualified, scan[at].Line, scan[test.Close].EndLine, ExtractText.Signature($"{test.Written}({name})"))
        {
            SubKind = test.Group ? "testGroup" : "test",
            Scope = _scope,
            Parent = parent,
            Summary = scan == _scan ? ScanComments.Summary(scan, at) : null,
            Test = true,
            SeenIn = _module ? _path : null,
        });

        Calls(text, pieces[0].Start, test.Close, key, locals, owner, depth + 1);
        return test.Close;
    }

    /// <summary>
    /// Whether code nested <paramref name="depth"/> templates, elements and test groups deep is read one deeper: not past
    /// <see cref="ScanFrame.MostDepth"/>, where what is nested there is marked dynamic at <paramref name="line"/>.
    /// </summary>
    private bool Deeper(int depth, int line)
    {
        if (depth < ScanFrame.MostDepth)
        {
            return true;
        }

        _extract.Dynamic(line, $"code nested more than {ScanFrame.MostDepth} templates, elements and test groups deep, which is not read");
        return false;
    }

    /// <summary>The key of the node what the file's top does outside its declarations is the calls of: made the first time it is needed.</summary>
    private string ModuleKey()
    {
        if (_moduleKey is not null)
        {
            return _moduleKey;
        }

        var qualified = $"{_path}.(module)";

        return _moduleKey = _extract.Add(new ExtractNode(Id(qualified), CodeNodeKinds.Member, "(module)", qualified, 1, Math.Max(1, _text.Lines.Starts.Count), "(module)")
        {
            SubKind = "module",
            Scope = _scope,
            SeenIn = _module ? _path : null,
        });
    }

    // ---- Exports a list or an assignment names ----

    /// <summary>
    /// What the file exports by a list, an assignment or a default, once it is read: each declaration at its top so named
    /// exported - by its own name, seen from everywhere; by another, that name its importers' (<see cref="ExtractNode.Exports"/>)
    /// and its own still the module's - and each name an import binds made visible to the file's importers as an export of
    /// the file it imports.
    /// </summary>
    private void Exported()
    {
        foreach (var (local, exported, line) in _exported)
        {
            if (_topKeys.TryGetValue(local, out var keys))
            {
                foreach (var key in keys)
                {
                    _extract.Change(key, node => exported == local
                        ? node with { SeenIn = null }
                        : node with { Exports = [.. (node.Exports ?? []).Append(exported).Distinct(StringComparer.Ordinal)] });
                }
            }
            else if (_bindings.TryGetValue(local, out var binding) && binding.Export is { } name)
            {
                Run(binding.Specifier, line, null, [name], export: true);
            }
        }
    }

    // ---- Names and types ----

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

    /// <summary>The names joined by dots at <paramref name="at"/>, joined by dots, and the index past the last; no chain where no name is there.</summary>
    private static (string? Chain, int Past) Chain(ScannedText scan, int at, int end)
    {
        var (names, past) = ChainAt(scan, at, end);
        return (names.Count > 0 ? string.Join('.', names.Select(index => scan.Text(index))) : null, past);
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
    /// A reference of <paramref name="kind"/> to the type <paramref name="chain"/> names: its last name, qualified by the
    /// names before it - an import's prefix where one is them, a type's otherwise - a default or renamed import's name
    /// written as the export it binds.
    /// </summary>
    private void Mention(string key, string kind, string chain, int line)
    {
        var names = Expanded(chain.Split('.'));

        if (names.Count == 1)
        {
            _extract.Reference(key, kind, names[0], line);
            return;
        }

        var qualifier = names.Count == 2 && _bindings.TryGetValue(names[0], out var binding) && binding.Prefixed
            ? new ExtractQualifier(CodeQualifierKinds.Prefix, names[0])
            : new ExtractQualifier(CodeQualifierKinds.Type, string.Join('.', names[..^1]));

        _extract.Reference(key, kind, names[^1], line, qualifier: qualifier);
    }

    /// <summary>
    /// A <c>references</c> edge from the node of <paramref name="key"/> to each type the tokens from <paramref name="start"/>
    /// to <paramref name="end"/> name that the language does not declare, that no type parameter in force is - a generic
    /// function type's own (<c>&lt;T&gt;(item: T) =&gt; T</c>) among them - and that is no member's or parameter's name of an
    /// object or a function type, nor a value <c>typeof</c> takes.
    /// </summary>
    private void TypeReferences(string key, int start, int end)
    {
        var own = new HashSet<string>(StringComparer.Ordinal);

        for (var at = start; at < end && at < _scan.Count; at = _scan.Next(at))
        {
            // A list parameters follow, in a type: a signature's own type parameters - <T>(x: T) => T, new <T>(), map<U>(f): U.
            if (_scan.Is(at, "<") && _scan.Is(Angle(_scan, at, end), "("))
            {
                own.UnionWith(TypeParameters(at, end));
            }
        }

        for (var at = start; at < end && at < _scan.Count; at = _scan.Next(at))
        {
            if (!_scan.IsName(at) || TypeWords.Contains(_scan.Text(at)) || Builtins.Contains(_scan.Text(at)) || _typeParameters.Contains(_scan.Text(at))
                || own.Contains(_scan.Text(at)) || IsMember(_scan, _scan.Previous(at)) || Reserved.Contains(_scan.Text(at)))
            {
                continue;
            }

            var next = _scan.Next(at);
            var previous = _scan.Previous(at);

            // { a: T }, { a?: T }, (x: T) => U, [K in keyof T], x is T, infer U, typeof x: no type's name.
            if (_scan.Is(next, ":") || (_scan.Is(next, "?") && _scan.Is(_scan.Next(next), ":")) || _scan.Is(next, "in") || _scan.Is(next, "is")
                || _scan.Is(previous, "infer") || _scan.Is(previous, "typeof") || _scan.Is(previous, "..."))
            {
                at = _scan.Is(previous, "typeof") ? Math.Max(at, _scan.Previous(ChainAt(_scan, at, end).Past)) : at;
                continue;
            }

            var (chain, past) = Chain(_scan, at, end);

            if (chain is not null)
            {
                Mention(key, CodeEdgeKinds.References, chain, _scan[at].Line);
            }

            at = Math.Max(at, _scan.Previous(past));
        }
    }

    /// <summary>
    /// What a declaration's type from <paramref name="start"/> to <paramref name="end"/> is, as a type qualifier names it:
    /// the chain of its name, its arguments and a union with <c>null</c> or <c>undefined</c> left out - <c>any</c> as it is;
    /// <see langword="null"/> where it is no name's - a union of more, an object, a function, an array.
    /// </summary>
    private string? TypeKey(ScannedText scan, int start, int end)
    {
        if (start < 0 || start >= end || !scan.IsName(start) || TypeWords.Contains(scan.Text(start)))
        {
            return null;
        }

        var (names, past) = ChainAt(scan, start, end);

        if (scan.Is(past, "<"))
        {
            past = Angle(scan, past, end);
        }

        while (scan.Is(past, "|") && (scan.Is(scan.Next(past), "null") || scan.Is(scan.Next(past), "undefined")))
        {
            past = scan.Next(scan.Next(past));
        }

        return past < end ? null : string.Join('.', Expanded([.. names.Select(index => scan.Text(index))]));
    }

    /// <summary>Whether the type from <paramref name="start"/> to <paramref name="end"/> is a function's: a function type's arrow at its top, or <c>Function</c>.</summary>
    private bool IsFunctionType(int start, int end) => IsFunctionType(_scan, start, end);

    private static bool IsFunctionType(ScannedText scan, int start, int end)
    {
        if (start < 0 || start >= end)
        {
            return false;
        }

        if (scan.Is(start, "Function") && scan.Next(start) >= end)
        {
            return true;
        }

        var at = scan.Is(start, "<") ? Angle(scan, start, end) : start;
        return scan.Is(at, "(") && scan.Is(Math.Min(scan.Past(at), end), "=>");
    }

    /// <summary>Whether the token at <paramref name="at"/> makes what follows it a member of what precedes it: <c>.</c> or <c>?.</c>.</summary>
    private static bool IsMember(ScannedText scan, int at) => at >= 0 && at < scan.Count && Accessors.Contains(scan.Text(at));

    /// <summary><paramref name="at"/>, or the token before it where it is TypeScript's non-null assertion <c>!</c>.</summary>
    private static int Unasserted(ScannedText scan, int at) => scan.Is(at, "!") ? scan.Previous(at) : at;

    /// <summary>The index of the bracket that closes the one at <paramref name="open"/>; the text's end where none does.</summary>
    private static int CloseOf(ScannedText scan, int open) => scan.Match(open) is var close and >= 0 ? close : scan.Count;

    /// <summary>Whether the token at <paramref name="at"/> is a template literal: a text that begins with a backquote.</summary>
    private static bool IsTemplate(ScannedText scan, int at) => IsText(scan, at) && scan.Source[scan[at].Start] == '`';

    private static bool IsText(ScannedText scan, int at) => at >= 0 && at < scan.Count && scan[at].Kind == ScanTokenKind.Text;

    private bool IsText(int at) => IsText(_scan, at);

    /// <summary>The specifier the text at <paramref name="at"/> of <paramref name="scan"/> holds (<see cref="Specifier(int)"/>).</summary>
    private static string? SpecifierOf(ScannedText scan, int at)
    {
        if (!IsText(scan, at))
        {
            return null;
        }

        var text = scan.Text(at);
        return text.StartsWith('`') && text.Contains("${", StringComparison.Ordinal) ? null : Unquoted(text) is { Length: > 0 } unquoted ? unquoted : null;
    }

    /// <summary>
    /// The specifier the call whose parenthesis is at <paramref name="open"/> is given as its whole first argument - one
    /// text, what follows it the call's end or its next argument; <see langword="null"/> where the code computes it:
    /// <c>require('./' + name)</c>, a template with an interpolation.
    /// </summary>
    private static string? WholeSpecifier(ScannedText scan, int open)
    {
        var argument = scan.Next(open);
        var after = scan.Next(argument);
        return after == CloseOf(scan, open) || scan.Is(after, ",") ? SpecifierOf(scan, argument) : null;
    }

    /// <summary>Whether <paramref name="name"/> is written as a type's is: its first letter, past any <c>_</c> or <c>$</c>, a capital.</summary>
    private static bool IsTypeName(string name) => name.TrimStart('_', '$') is { Length: > 0 } trimmed && char.IsUpper(trimmed[0]);

    /// <summary>The id of a declaration of the qualified name <paramref name="qualified"/>.</summary>
    private string Id(string qualified) => _prefix + qualified;

    /// <summary>The header from the token at <paramref name="start"/> to the one before <paramref name="end"/>, on one line, as a node's signature.</summary>
    private string Signature(int start, int end) => ExtractText.Signature(ExtractText.Flat(_scan.Spelled(start, end)));

    /// <summary>The index of the token before <paramref name="past"/>, within the text.</summary>
    private int LastOf(int past) => Math.Clamp(past - 1, 0, Math.Max(_scan.Count - 1, 0));
}
