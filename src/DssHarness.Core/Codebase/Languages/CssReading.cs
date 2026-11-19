using System.Text;
using DssHarness.Core.Codebase.Languages.Scanning;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// One style sheet's reading over the structural scanner's text (<see cref="CssLexer"/>): its statements walked block by
/// block, each block the scan's matched braces hold, each line a token's, and no block nested past
/// <see cref="ScanFrame.MostDepth"/> read. A statement is an at-rule, by its word; a declaration, a name and its colon,
/// whose value runs to its semicolon or its block's close; or a rule, a selector and the block it opens - a nested
/// rule read inside the block that holds it, each rule's lines its own to its own close. Every reference is made from
/// the innermost node that holds it: the rule whose block it is written in - the subject its selector styles - the
/// mixin, the function, the keyframes, or the variable its value is given to; what no node holds, from the sheet's
/// first node.
/// </summary>
internal sealed class CssReading
{
    /// <summary>The pseudo-classes whose arguments are selectors of the element itself, read as the selector's own parts.</summary>
    private static readonly HashSet<string> Alternatives = new(StringComparer.OrdinalIgnoreCase) { "is", "where", "matches", "any", "-webkit-any", "-moz-any", "host", "slotted" };

    /// <summary>The prefixes a vendor writes before an at-rule's word or a property's name, as <c>@-webkit-keyframes</c>.</summary>
    private static readonly string[] Vendors = ["-webkit-", "-moz-", "-ms-", "-o-"];

    /// <summary>
    /// The words an <c>animation</c>'s value gives a meaning of their own - its easings, its counts, its directions, its
    /// fills, its states and the values every property takes - which name no keyframes, as CSS reads the shorthand.
    /// </summary>
    private static readonly HashSet<string> AnimationWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "none", "linear", "ease", "ease-in", "ease-out", "ease-in-out", "step-start", "step-end", "infinite", "normal",
        "reverse", "alternate", "alternate-reverse", "forwards", "backwards", "both", "running", "paused", "auto",
        "initial", "inherit", "unset", "revert", "revert-layer",
    };

    private readonly string _language;
    private readonly string _path;
    private readonly StyleSyntax _syntax;
    private readonly bool _module;
    private readonly InterpolatedText _text;
    private readonly ScannedText _scan;
    private readonly ScanExtract _extract = new();

    /// <summary>The key of each node, by its name: one node a name in a sheet, where it is first declared.</summary>
    private readonly Dictionary<string, string> _named = new(StringComparer.Ordinal);

    /// <summary>The references, in the order the text makes them, each from the key of the node that holds it - none where no node does.</summary>
    private readonly List<(string? From, string Kind, string Name, int Line, ExtractQualifier? Qualifier)> _references = [];

    /// <summary>The key of the sheet's first node: what a reference no node holds is made from.</summary>
    private string? _first;

    /// <summary>Reads <paramref name="text"/>, the sheet at <paramref name="path"/>, as part of the extract of <paramref name="language"/>.</summary>
    /// <param name="language">The implementation the extract is part of: the prefix of every id.</param>
    /// <param name="path">The file's path.</param>
    /// <param name="text">The sheet's text, at the lines it stands on in its file.</param>
    /// <param name="syntax">What it is written in.</param>
    /// <param name="module">Whether it is read as a CSS module, each class, id and keyframes exported by its name to the scripts that import it.</param>
    public CssReading(string language, string path, string text, StyleSyntax syntax, bool module)
    {
        _language = language;
        _path = path;
        _syntax = syntax;
        _module = module;
        _text = CssLexer.Scan(syntax == StyleSyntax.Sass ? CssLexer.Braced(text) : text, syntax);
        _scan = _text.Scan;
    }

    /// <summary>The sheet's extract.</summary>
    public FileExtract Read()
    {
        _extract.Generated = ScanComments.Generated(_scan);
        Block(0, _scan.Count, ScanFrame.Top, [], keyframes: false);

        foreach (var (from, kind, name, line, qualifier) in _references)
        {
            if ((from ?? _first) is { } made)
            {
                _extract.Reference(made, kind, name, line, qualifier: qualifier);
            }
        }

        return _extract.Build();
    }

    /// <summary>
    /// The statements from the token at <paramref name="from"/> to the one before <paramref name="to"/>, in
    /// <paramref name="frame"/> - its parent the node a reference is made from - <paramref name="parents"/> the selectors
    /// a <c>&amp;</c> stands for, each by the part a suffix is written to; a keyframes' block where
    /// <paramref name="keyframes"/>, whose selectors are no rules. A block nested past <see cref="ScanFrame.MostDepth"/>
    /// is not read, and its line marked dynamic.
    /// </summary>
    private void Block(int from, int to, ScanFrame frame, IReadOnlyList<string> parents, bool keyframes)
    {
        if (frame.TooDeep)
        {
            _extract.Dynamic(LineOf(from), ScanFrame.TooDeepWhy);
            return;
        }

        ScanFrame.Walk(from, to, at => Statement(at, to, frame, parents, keyframes));
    }

    /// <summary>The statement that begins at <paramref name="at"/>, within <paramref name="to"/>; answers where the next begins.</summary>
    private int Statement(int at, int to, ScanFrame frame, IReadOnlyList<string> parents, bool keyframes)
    {
        // A semicolon alone ends nothing, and a close no brace of this block opened is passed over.
        if (_scan.Is(at, ";") || _scan.Is(at, "}"))
        {
            return at + 1;
        }

        var stop = HeadEnd(at, to);
        var opens = stop < to && _scan.Is(stop, "{");
        var (close, past) = opens ? _scan.Bounds(stop, to) : (stop, stop < to && _scan.Is(stop, ";") ? stop + 1 : stop);
        var head = new Head(at, stop, opens, close, past);

        if (AtWord(head) is { } word)
        {
            At(word, head, frame, parents);
            return past;
        }

        if (Colon(at, stop) is var colon and >= 0 && IsDeclaration(head, colon))
        {
            return Declaration(head, colon, to, frame, parents);
        }

        if (!opens)
        {
            Called(head, frame, parents);
            return past;
        }

        if (keyframes)
        {
            // A keyframe's selector - from, to, a percentage - is no rule: its declarations are the keyframes'.
            Block(stop + 1, close, frame.Deeper(), parents, keyframes: true);
            return past;
        }

        Rule(head, head.Start, frame, parents);
        return past;
    }

    /// <summary>
    /// One statement's head and what ends it: from <paramref name="Start"/> to <paramref name="Stop"/> - the <c>;</c>,
    /// <c>{</c> or <c>}</c> after it, or its block's end - whether a block opens there, the index of that block's close and
    /// the index past the statement.
    /// </summary>
    private readonly record struct Head(int Start, int Stop, bool Opens, int Close, int Past);

    /// <summary>The index of the <c>;</c>, <c>{</c> or <c>}</c> that ends the head beginning at <paramref name="at"/>, its brackets passed over; <paramref name="to"/> where none does.</summary>
    private int HeadEnd(int at, int to)
    {
        for (var index = at; index < to; index++)
        {
            if (_scan.Is(index, ";") || _scan.Is(index, "{") || _scan.Is(index, "}"))
            {
                return index;
            }

            if (_scan.Is(index, "(") || _scan.Is(index, "["))
            {
                index = _scan.Bounds(index, to).Past - 1;
            }
        }

        return to;
    }

    /// <summary>The index past the value that begins at <paramref name="at"/>: at its <c>;</c> or its block's close, every bracket - a brace among them - passed over.</summary>
    private int ValueEnd(int at, int to)
    {
        for (var index = at; index < to; index++)
        {
            if (_scan.Is(index, ";") || _scan.Is(index, "}"))
            {
                return index;
            }

            if (_scan.Opens(index))
            {
                index = _scan.Bounds(index, to).Past - 1;
            }
        }

        return to;
    }

    /// <summary>The index of the first colon of the head from <paramref name="at"/> to <paramref name="stop"/>, its brackets passed over; -1 where none is.</summary>
    private int Colon(int at, int stop)
    {
        for (var index = at; index < stop; index++)
        {
            if (_scan.Is(index, ":"))
            {
                return index;
            }

            if (_scan.Is(index, "(") || _scan.Is(index, "["))
            {
                index = _scan.Bounds(index, stop).Past - 1;
            }
        }

        return -1;
    }

    // ---- At-rules ----

    /// <summary>
    /// The word of the at-rule <paramref name="head"/> is, lower-cased, a vendor's prefix left out; <see langword="null"/>
    /// where it is none: a Less variable's <c>@name:</c> is a declaration, and its <c>@name();</c> a call. The indented
    /// syntax's <c>=name</c> is a <c>@mixin</c>, and its <c>+name</c> an <c>@include</c>.
    /// </summary>
    private string? AtWord(Head head)
    {
        var at = head.Start;

        if (_syntax == StyleSyntax.Sass && (_scan.Is(at, "=") || _scan.Is(at, "+")) && _scan.IsName(at + 1) && Adjacent(at + 1))
        {
            return _scan.Is(at, "=") ? "mixin" : "include";
        }

        if (!_scan.IsName(at) || _scan.Text(at) is not ['@', _, ..] written)
        {
            return null;
        }

        if (_scan.Is(at + 1, ":") || (!head.Opens && _scan.Is(at + 1, "(") && Adjacent(at + 1)))
        {
            return null;
        }

        return Unvendored(written[1..]);
    }

    /// <summary><paramref name="word"/> lower-cased, the prefix a vendor writes before it left out.</summary>
    private static string Unvendored(string word)
    {
        var lower = word.ToLowerInvariant();
        return Vendors.FirstOrDefault(vendor => lower.StartsWith(vendor, StringComparison.Ordinal)) is { } vendor ? lower[vendor.Length..] : lower;
    }

    /// <summary>One at-rule, by its <paramref name="word"/>.</summary>
    private void At(string word, Head head, ScanFrame frame, IReadOnlyList<string> parents)
    {
        var at = head.Start;
        var line = LineOf(at);
        var after = at + 1;

        switch (word)
        {
            case "import":
                Imports(head, after, word, prefix: null, export: false, show: null, hide: null);
                return;

            case "use":
                Use(head, after);
                return;

            case "forward":
                Forward(head, after);
                return;

            case "mixin" or "function":
                if (_scan.IsName(after) && Plain(after) is { } routine)
                {
                    var key = Node(routine, word == "mixin" ? CodeSubKinds.StyleMixin : CodeSubKinds.StyleFunction, head, Signature(head), exports: false);
                    Parameters(after + 1, head.Stop, key);
                    Body(head, frame.Inside(null, null, key, null), [], keyframes: false);
                }
                else
                {
                    Body(head, frame.Deeper(), parents, keyframes: false);
                }

                return;

            case "include":
                Included(head, after, frame);
                Body(head, frame.Deeper(), parents, keyframes: false);
                return;

            case "extend":
                foreach (var part in Selector(after, head.Stop, parents, 0).Parts)
                {
                    _references.Add((frame.Parent, CodeEdgeKinds.Inherits, part.Name, line, MarkupDialect.Selector));
                }

                return;

            case "apply":
                Applied(head, after, frame.Parent);
                return;

            case "keyframes":
                if (Named(after, head.Stop) is { } animation)
                {
                    var key = Node(animation, CodeSubKinds.Keyframes, head, Signature(head), exports: true);
                    Body(head, frame.Inside(null, null, key, null), [], keyframes: true);
                }
                else
                {
                    Body(head, frame.Deeper(), [], keyframes: true);
                }

                return;

            case "font-face":
                if (head.Opens && FontFamily(head.Stop + 1, head.Close) is { } family)
                {
                    var key = Node(family, CodeSubKinds.FontFace, head, Signature(head), exports: false);
                    Body(head, frame.Inside(null, null, key, null), parents, keyframes: false);
                }
                else
                {
                    Body(head, frame.Deeper(), parents, keyframes: false);
                }

                return;

            case "property":
                if (_scan.IsName(after) && _scan.Text(after) is ['-', '-', ..] property)
                {
                    var key = Node(property, CodeSubKinds.StyleVariable, head, Signature(head), exports: false);
                    Body(head, frame.Inside(null, null, key, null), parents, keyframes: false);
                }
                else
                {
                    Body(head, frame.Deeper(), parents, keyframes: false);
                }

                return;

            case "at-root" when after < head.Stop && !_scan.Is(after, "(") && head.Opens:
                // A rule written at the sheet's root from inside another: its selector is the rest of the head.
                Rule(head, after, frame, parents);
                return;

            case "each" or "for":
                // The variables a loop binds are its own: what it uses is past its in or its from.
                var binds = after;

                while (binds < head.Stop && !_scan.Is(binds, "in") && !_scan.Is(binds, "from"))
                {
                    binds++;
                }

                Uses(binds, head.Stop, frame.Parent);
                Body(head, frame.Deeper(), parents, keyframes: false);
                return;

            default:
                // A conditional group - @media, @supports, @container, @layer, @if - and every other: its head read for
                // what it uses, its block read as the block that holds it reads one.
                Uses(after, head.Stop, frame.Parent);
                Body(head, frame.Deeper(), parents, keyframes: false);
                return;
        }
    }

    /// <summary>The block <paramref name="head"/> opens, read in <paramref name="frame"/>; nothing where it opens none.</summary>
    private void Body(Head head, ScanFrame frame, IReadOnlyList<string> parents, bool keyframes)
    {
        if (head.Opens)
        {
            Block(head.Stop + 1, head.Close, frame, parents, keyframes);
        }
    }

    /// <summary>
    /// A <c>@mixin</c>'s or a <c>@function</c>'s parameters, from the token at <paramref name="at"/>: each parameter's name
    /// its own, and what its default uses a use, made from <paramref name="by"/>.
    /// </summary>
    private void Parameters(int at, int stop, string by)
    {
        if (!_scan.Is(at, "("))
        {
            Uses(at, stop, by);
            return;
        }

        var (inner, past) = _scan.Bounds(at, stop);

        foreach (var (start, end) in Split(at + 1, inner))
        {
            Uses(start < end && _scan.IsName(start) && _scan.Text(start)[0] is '$' or '@' ? start + 1 : start, end, by);
        }

        Uses(past, stop, by);
    }

    /// <summary>
    /// An <c>@include</c>: a use of the mixin it names - through the module its namespace names, where it writes one - and
    /// of what its arguments use; the variables its <c>using</c> binds are its own.
    /// </summary>
    private void Included(Head head, int at, ScanFrame frame)
    {
        var stop = head.Stop;

        for (var index = at; index < head.Stop; index++)
        {
            if (_scan.Is(index, "using"))
            {
                stop = index;
                break;
            }
        }

        if (!_scan.IsName(at))
        {
            Uses(at, stop, frame.Parent);
            return;
        }

        var line = LineOf(at);

        if (_scan.Is(at + 1, ".") && Adjacent(at + 1) && _scan.IsName(at + 2) && Adjacent(at + 2))
        {
            _references.Add((frame.Parent, CodeEdgeKinds.Uses, _scan.Text(at + 2), line, new ExtractQualifier(CodeQualifierKinds.Prefix, _scan.Text(at))));
            Uses(at + 3, stop, frame.Parent);
            return;
        }

        _references.Add((frame.Parent, CodeEdgeKinds.Uses, _scan.Text(at), line, new ExtractQualifier(CodeQualifierKinds.StyleMixin)));
        Uses(at + 1, stop, frame.Parent);
    }

    /// <summary>
    /// Tailwind's <c>@apply</c>: each class it applies a use of the rule of that name - the class past a variant's
    /// <c>hover:</c>, an <c>!important</c>'s mark left out - as markup's <c>class</c> is.
    /// </summary>
    private void Applied(Head head, int at, string? from)
    {
        if (at >= head.Stop)
        {
            return;
        }

        var line = LineOf(at);
        var written = _scan.Source[_scan[at].Start.._scan[head.Stop - 1].End];

        foreach (var word in written.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var applied = Variantless(word).Trim('!');

            if (applied.Length > 0 && applied != "important" && !applied.Contains("#{", StringComparison.Ordinal) && !applied.Contains("@{", StringComparison.Ordinal))
            {
                _references.Add((from, CodeEdgeKinds.Uses, $".{applied}", line, MarkupDialect.Selector));
            }
        }

        Uses(at, head.Stop, from);
    }

    /// <summary><paramref name="word"/> past the last variant a colon ends outside brackets: <c>md:hover:flex</c> is the class <c>flex</c>.</summary>
    private static string Variantless(string word)
    {
        var depth = 0;
        var start = 0;

        for (var at = 0; at < word.Length; at++)
        {
            depth += word[at] is '[' or '(' ? 1 : word[at] is ']' or ')' ? -1 : 0;

            if (word[at] == ':' && depth == 0)
            {
                start = at + 1;
            }
        }

        return word[start..];
    }

    /// <summary>The name a <c>@keyframes</c> gives its animation: a name, or a string; <see langword="null"/> where an interpolation writes it.</summary>
    private string? Named(int at, int stop)
    {
        if (at >= stop)
        {
            return null;
        }

        if (_scan[at].Kind == ScanTokenKind.Text && _scan.Text(at) is ['"' or '\'', ..])
        {
            return Unquoted(at);
        }

        if (Interpolated(at, stop))
        {
            _extract.Dynamic(LineOf(at), "a name an interpolation writes, whose node is not read");
            return null;
        }

        return _scan.IsName(at) ? CssLexer.Unescaped(_scan.Text(at)) : null;
    }

    /// <summary>The family a <c>@font-face</c>'s block names in its <c>font-family</c>, from the token at <paramref name="from"/> to <paramref name="to"/>; <see langword="null"/> where it names none.</summary>
    private string? FontFamily(int from, int to)
    {
        for (var at = from; at + 1 < to; at++)
        {
            if (_scan.IsName(at) && string.Equals(_scan.Text(at), "font-family", StringComparison.OrdinalIgnoreCase) && _scan.Is(at + 1, ":"))
            {
                var value = at + 2;

                if (Unquoted(value) is { Length: > 0 } quoted)
                {
                    return quoted;
                }

                var words = new List<string>();

                for (; value < to && _scan.IsName(value); value++)
                {
                    words.Add(_scan.Text(value));
                }

                return words.Count > 0 ? string.Join(' ', words) : null;
            }

            if (_scan.Opens(at))
            {
                at = _scan.Bounds(at, to).Past - 1;
            }
        }

        return null;
    }

    // ---- Imports ----

    /// <summary>
    /// The files an <c>@import</c>, a <c>@use</c> or a <c>@forward</c> from the token at <paramref name="from"/> names,
    /// each of its comma's parts one: an import of each file it may be - the first the tree holds taken - by
    /// <see cref="Candidates"/>; none of a package, a URL, a host's path or a module the language builds in.
    /// </summary>
    private void Imports(Head head, int from, string word, string? prefix, bool export, IReadOnlyList<string>? show, IReadOnlyList<string>? hide)
    {
        foreach (var (start, end) in Split(from, head.Stop))
        {
            if (Target(start, end) is not { } target)
            {
                continue;
            }

            var line = LineOf(head.Start);
            var fallback = false;

            foreach (var candidate in Candidates(target, word))
            {
                _extract.Import(new ExtractImport(CodeImportForms.Relative, candidate, line) { Fallback = fallback, Prefix = prefix, Export = export, Show = show, Hide = hide });
                fallback = true;
            }
        }
    }

    /// <summary>A <c>@use</c>: an import of its module by its namespace - the name its <c>as</c> gives, its file's own name where none does, none after <c>as *</c>.</summary>
    private void Use(Head head, int from)
    {
        var end = head.Stop;

        for (var at = from; at < head.Stop; at++)
        {
            if (_scan.Is(at, "as") || _scan.Is(at, "with"))
            {
                end = at;
                break;
            }
        }

        if (Target(from, end) is not { } target)
        {
            return;
        }

        var prefix = Namespace(target);

        if (end < head.Stop && _scan.Is(end, "as"))
        {
            prefix = _scan.Is(end + 1, "*") ? null : _scan.IsName(end + 1) ? _scan.Text(end + 1) : prefix;
        }

        Imports(head with { Stop = end }, from, "use", prefix, export: false, show: null, hide: null);
    }

    /// <summary>A <c>@forward</c>: an export of what its module makes visible, its <c>show</c> and <c>hide</c> lists its filters.</summary>
    private void Forward(Head head, int from)
    {
        var end = head.Stop;
        List<string>? show = null;
        List<string>? hide = null;

        for (var at = from; at < head.Stop; at++)
        {
            if (_scan.Is(at, "show") || _scan.Is(at, "hide") || _scan.Is(at, "as") || _scan.Is(at, "with"))
            {
                end = Math.Min(end, at);
            }

            if (_scan.Is(at, "show") || _scan.Is(at, "hide"))
            {
                var listed = _scan.Is(at, "show") ? show ??= [] : hide ??= [];

                for (var name = at + 1; name < head.Stop && !_scan.Is(name, "show") && !_scan.Is(name, "hide") && !_scan.Is(name, "as") && !_scan.Is(name, "with"); name++)
                {
                    if (_scan.IsName(name))
                    {
                        listed.Add(_scan.Text(name));
                    }
                }
            }
        }

        Imports(head with { Stop = end }, from, "forward", prefix: null, export: true, show, hide);
    }

    /// <summary>
    /// The path the part of an import's head from <paramref name="start"/> to <paramref name="end"/> names: its first string
    /// or <c>url(...)</c> - past Less's options in brackets - or, in the indented syntax, the path it writes unquoted;
    /// <see langword="null"/> where it names none, or one an interpolation writes.
    /// </summary>
    private string? Target(int start, int end)
    {
        if (start < end && _scan.Is(start, "("))
        {
            start = _scan.Bounds(start, end).Past;
        }

        if (start >= end)
        {
            return null;
        }

        if (_scan[start].Kind == ScanTokenKind.Text)
        {
            return Unquoted(start) ?? Url(start);
        }

        if (_scan.Is(start, "url") && _scan.Is(start + 1, "(") && Unquoted(start + 2) is { } quoted)
        {
            return quoted;
        }

        if (_syntax == StyleSyntax.Sass && !Interpolated(start, end))
        {
            return _scan.Source[_scan[start].Start.._scan[end - 1].End].Trim();
        }

        return null;
    }

    /// <summary>
    /// The files <paramref name="target"/> may be, in the order they are tried, as the sheet's language finds an
    /// <c>@import</c>, a <c>@use</c> or a <c>@forward</c> (<paramref name="word"/>) of it: a path with its own extension as
    /// written; with none, CSS's as written, Less's with <c>.less</c>, and Sass's by its load-path rule - each of Sass's
    /// extensions, the sheet's own first, then <c>.css</c>, each before its partial's <c>_name</c>; an <c>@import</c>'s
    /// <c>.import</c> file first; then the directory's <c>index</c> and <c>_index</c>. None for a URL, a host's or a
    /// package's path, or a module Sass builds in (<c>sass:math</c>).
    /// </summary>
    private IReadOnlyList<string> Candidates(string target, string word)
    {
        var cut = target.IndexOfAny(['?', '#']);
        target = (cut >= 0 ? target[..cut] : target).Trim();

        if (target.Length == 0 || target[0] is '/' or '~' || target.Contains("//", StringComparison.Ordinal) || SchemeOf(target) || target.Contains('\\', StringComparison.Ordinal))
        {
            return [];
        }

        var slash = target.LastIndexOf('/');
        var directory = target[..(slash + 1)];
        var name = target[(slash + 1)..];
        var dot = name.LastIndexOf('.');
        var extension = dot > 0 ? name[dot..].ToLowerInvariant() : string.Empty;

        if (name.Length == 0)
        {
            return [];
        }

        switch (_syntax)
        {
            case StyleSyntax.Css:
                return [target];

            case StyleSyntax.Less:
                return [extension.Length > 0 ? target : $"{target}.less"];
        }

        if (extension is ".css" or ".scss" or ".sass")
        {
            return extension == ".css" || name.StartsWith('_') ? [target] : [target, $"{directory}_{name}"];
        }

        string[] extensions = _syntax == StyleSyntax.Sass ? [".sass", ".scss", ".css"] : [".scss", ".sass", ".css"];
        var tried = new List<string>();

        if (word == "import")
        {
            foreach (var own in extensions[..2])
            {
                tried.Add($"{target}.import{own}");
                tried.Add($"{directory}_{name}.import{own}");
            }
        }

        foreach (var own in extensions)
        {
            tried.Add($"{target}{own}");
            tried.Add($"{directory}_{name}{own}");
        }

        foreach (var own in extensions)
        {
            tried.Add($"{target}/index{own}");
            tried.Add($"{target}/_index{own}");
        }

        return tried;
    }

    /// <summary>Whether <paramref name="target"/> begins with a scheme - <c>https:</c>, <c>data:</c>, Sass's own <c>sass:</c>, a package importer's <c>pkg:</c>.</summary>
    private static bool SchemeOf(string target)
    {
        var colon = target.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 && target[..colon].All(character => char.IsAsciiLetterOrDigit(character) || character is '+' or '-' or '.');
    }

    /// <summary>The namespace a <c>@use</c> of <paramref name="target"/> gives its module where it says none: its file's name, without a partial's <c>_</c> or any extension.</summary>
    private static string? Namespace(string target)
    {
        var name = target[(target.LastIndexOf('/') + 1)..].TrimStart('_');
        var dot = name.IndexOf('.', StringComparison.Ordinal);
        name = dot >= 0 ? name[..dot] : name;
        return name.Length > 0 ? name : null;
    }

    // ---- Rules ----

    /// <summary>
    /// A rule: each simple part of its selector, from the token at <paramref name="selector"/> to the head's end, a node
    /// over the rule's lines - <paramref name="head"/>'s, to its block's own close - its block read with the rule's
    /// subject the node its references are made from.
    /// </summary>
    private void Rule(Head head, int selector, ScanFrame frame, IReadOnlyList<string> parents)
    {
        var read = Selector(selector, head.Stop, parents, 0);
        var signature = Signature(head);
        string? subject = null;
        string? first = null;

        foreach (var part in read.Parts)
        {
            var key = Node(part.Name, CodeSubKinds.Rule, head, signature, exports: true);
            first ??= key;

            if (part.Subject)
            {
                subject ??= key;
            }
        }

        subject ??= first ?? frame.Parent;

        foreach (var extended in read.Extended)
        {
            _references.Add((subject, CodeEdgeKinds.Inherits, extended, LineOf(head.Start), MarkupDialect.Selector));
        }

        if (read.Interpolated)
        {
            _extract.Dynamic(LineOf(selector), "a selector an interpolation writes, whose rule is no node");
        }

        foreach (var hole in read.Holes)
        {
            Uses(hole, hole + 1, subject);
        }

        Block(head.Stop + 1, head.Close, frame.Inside(null, null, subject, null), read.Lasts, keyframes: false);
    }

    /// <summary>
    /// A statement no block follows that is neither an at-rule nor a declaration: Less's call of a mixin (<c>.m();</c>,
    /// <c>#ns &gt; .m;</c>) a use of the rule it names, its <c>&amp;:extend(.b);</c> an inheritance of the rule it names;
    /// whatever else, read for what it uses.
    /// </summary>
    private void Called(Head head, ScanFrame frame, IReadOnlyList<string> parents)
    {
        if (_syntax == StyleSyntax.Less && head.Start < head.Stop && (_scan.Is(head.Start, ".") || _scan.Is(head.Start, "#") || _scan.Is(head.Start, "&")))
        {
            var read = Selector(head.Start, head.Stop, parents, 0);
            var line = LineOf(head.Start);

            foreach (var extended in read.Extended)
            {
                _references.Add((frame.Parent, CodeEdgeKinds.Inherits, extended, line, MarkupDialect.Selector));
            }

            if (read.Extended.Count == 0 && read.Parts.Count > 0)
            {
                _references.Add((frame.Parent, CodeEdgeKinds.Uses, read.Parts[^1].Name, line, MarkupDialect.Selector));
            }
        }

        Uses(head.Start, head.Stop, frame.Parent);
    }

    /// <summary>One simple part of a selector, and whether it is its subject's: the first part of the last compound that has one.</summary>
    private readonly record struct Part(string Name, bool Subject);

    /// <summary>
    /// What a selector holds: its simple parts, in order, each once; for each of its complex selectors the parts a nested
    /// rule's <c>&amp;</c> writes its suffix to; what a Less <c>:extend()</c> names; whether an interpolation writes a part,
    /// and the tokens of each interpolation it holds.
    /// </summary>
    private sealed record SelectorRead(List<Part> Parts, List<string> Lasts, List<string> Extended, List<int> Holes)
    {
        /// <summary>The index of each part among <see cref="Parts"/>, by its name.</summary>
        public Dictionary<string, int> Indices { get; } = new(StringComparer.Ordinal);

        /// <summary>Whether an interpolation writes a part.</summary>
        public bool Interpolated { get; set; }

        /// <summary>Whether a complex selector of it gave its subject.</summary>
        public bool Subjected { get; set; }
    }

    /// <summary>
    /// The compound selector a complex one is being read at: whether it has a part of its own yet, the names its last simple
    /// selector writes - one, or each of the parents' with the suffix a <c>&amp;</c> writes onto them - and whether a
    /// <c>&amp;</c> stands for the parents in it.
    /// </summary>
    private sealed class Compound
    {
        public bool Empty { get; private set; } = true;

        public IReadOnlyList<string> Tail { get; private set; } = [];

        public bool Parent { get; set; }

        /// <summary>A compound begins: nothing of it read yet.</summary>
        public void Reset() => (Empty, Tail, Parent) = (true, [], false);

        /// <summary>Its last simple selector writes <paramref name="names"/>.</summary>
        public void Wrote(IReadOnlyList<string> names) => (Empty, Tail) = (false, names);
    }

    /// <summary>The selector from the token at <paramref name="from"/> to <paramref name="to"/>, <paramref name="depth"/> arguments of a pseudo-class deep.</summary>
    private SelectorRead Selector(int from, int to, IReadOnlyList<string> parents, int depth)
    {
        var read = new SelectorRead([], [], [], []);

        foreach (var (start, end) in Split(from, to))
        {
            Complex(start, end, parents, depth, read);
        }

        return read;
    }

    /// <summary>
    /// One complex selector, from the token at <paramref name="start"/> to <paramref name="end"/>, read into
    /// <paramref name="read"/>: each compound's class (<c>.a</c>), id (<c>#a</c>), placeholder (<c>%a</c>) and element
    /// (<c>a</c>), as CSS reads it, escapes read; a <c>&amp;</c> the selectors <paramref name="parents"/> stands for, a
    /// suffix it writes making each of their parts a part of this one; the selectors an <c>:is()</c> or a
    /// <c>:where()</c> holds its own; no pseudo-class, attribute, combinator or Less mixin's parameters; and a part an
    /// interpolation writes none, said.
    /// </summary>
    private void Complex(int start, int end, IReadOnlyList<string> parents, int depth, SelectorRead read)
    {
        var compound = new Compound();
        var subject = -1;

        for (var at = start; at < end;)
        {
            if (at > start && !Adjacent(at))
            {
                compound.Reset();
            }

            if (_scan.Is(at, ">") || _scan.Is(at, "+") || _scan.Is(at, "~"))
            {
                compound.Reset();
                at++;
                continue;
            }

            if (_syntax == StyleSyntax.Less && _scan.Is(at, "when") && !Adjacent(at))
            {
                // A Less guard: what follows is a condition, no selector.
                break;
            }

            if (_scan[at].Kind == ScanTokenKind.Text)
            {
                at = Written(at, end, read);
                continue;
            }

            if ((_scan.Is(at, ".") || _scan.Is(at, "#")) && at + 1 < end && Adjacent(at + 1) && (_scan.IsName(at + 1) || _scan[at + 1].Kind == ScanTokenKind.Text))
            {
                if (Interpolated(at + 1, end))
                {
                    at = Written(at + 1, end, read);
                    continue;
                }

                Add(read, compound, ref subject, [_scan.Text(at) + CssLexer.Unescaped(_scan.Text(at + 1))]);
                at += 2;
                continue;
            }

            if (_scan.Is(at, "&"))
            {
                if (at + 1 < end && Adjacent(at + 1) && (_scan.IsName(at + 1) || _scan[at + 1].Kind == ScanTokenKind.Number))
                {
                    if (Interpolated(at + 1, end))
                    {
                        at = Written(at + 1, end, read);
                        continue;
                    }

                    var suffix = CssLexer.Unescaped(_scan.Text(at + 1));
                    Add(read, compound, ref subject, [.. parents.Select(written => written + suffix)]);
                    at += 2;
                    continue;
                }

                compound.Parent = true;
                at++;
                continue;
            }

            if (_scan.Is(at, ":"))
            {
                at = Pseudo(at, end, parents, depth, read, compound, ref subject);
                continue;
            }

            if (_scan.Is(at, "[") || _scan.Is(at, "("))
            {
                // An attribute's condition, or a Less mixin's parameters: no part of the selector.
                at = _scan.Bounds(at, end).Past;
                continue;
            }

            // An element's name begins a compound: one joined to a namespace's bar, an !important's mark or anything else is none.
            if (_scan.IsName(at) && (at == start || !Adjacent(at) || _scan.Is(at - 1, ">") || _scan.Is(at - 1, "+") || _scan.Is(at - 1, "~")))
            {
                var name = _scan.Text(at);

                if (Interpolated(at, end))
                {
                    at = Written(at, end, read);
                    continue;
                }

                if (name[0] is not ('$' or '@') && !name.StartsWith("--", StringComparison.Ordinal))
                {
                    Add(read, compound, ref subject, [CssLexer.Unescaped(name)]);
                }

                at++;
                continue;
            }

            at++;
        }

        // What a nested rule's & stands for: the names this complex's last simple selector writes, or the parents where
        // its last compound is the & alone.
        read.Lasts.AddRange(compound.Tail.Count > 0 ? compound.Tail : compound.Parent ? parents : []);

        if (subject >= 0 && !read.Subjected)
        {
            read.Parts[subject] = read.Parts[subject] with { Subject = true };
            read.Subjected = true;
        }
    }

    /// <summary>
    /// A pseudo-class or pseudo-element at the colon at <paramref name="at"/>: no part of its own; an <c>:is()</c>'s and
    /// the like's selectors the compound's own, read no deeper than <see cref="ScanFrame.MostDepth"/>; a Less
    /// <c>:extend()</c>'s what the rule inherits. Answers where the selector goes on.
    /// </summary>
    private int Pseudo(int at, int end, IReadOnlyList<string> parents, int depth, SelectorRead read, Compound compound, ref int subject)
    {
        var name = _scan.Is(at + 1, ":") ? at + 2 : at + 1;

        if (!_scan.IsName(name) || name >= end)
        {
            return name;
        }

        var group = name + 1;

        if (group >= end || !_scan.Is(group, "(") || !Adjacent(group))
        {
            return group;
        }

        var (inner, past) = _scan.Bounds(group, end);
        var pseudo = _scan.Text(name);

        if ((Alternatives.Contains(pseudo) || (_syntax == StyleSyntax.Less && pseudo == "extend")) && ScanFrame.ReadsDeeper(_extract, depth, LineOf(group), "selectors"))
        {
            var held = Selector(group + 1, inner, parents, depth + 1);
            read.Interpolated |= held.Interpolated;
            read.Holes.AddRange(held.Holes);

            if (pseudo == "extend")
            {
                read.Extended.AddRange(held.Parts.Select(part => part.Name).Where(part => part != "all"));
            }
            else if (held.Parts.Count > 0)
            {
                Add(read, compound, ref subject, [.. held.Parts.Select(part => part.Name)]);
            }
        }

        return past;
    }

    /// <summary>
    /// Adds the names a simple selector writes to the selector's parts - each once - and to <paramref name="compound"/>,
    /// whose first is the complex selector's subject while no later compound has a part.
    /// </summary>
    private static void Add(SelectorRead read, Compound compound, ref int subject, IReadOnlyList<string> names)
    {
        var first = true;

        foreach (var name in names.Where(name => name.Length > 0))
        {
            if (!read.Indices.TryGetValue(name, out var index))
            {
                read.Indices[name] = index = read.Parts.Count;
                read.Parts.Add(new Part(name, false));
            }

            if (first && compound.Empty)
            {
                subject = index;
            }

            first = false;
        }

        if (!first)
        {
            compound.Wrote(names);
        }
    }

    /// <summary>
    /// A simple selector an interpolation writes, from the token at <paramref name="at"/>: the run of the tokens joined to
    /// it - names, interpolations, numbers and dashes - none of a part's name, said; each interpolation kept for what it
    /// uses. Answers where the selector goes on.
    /// </summary>
    private int Written(int at, int end, SelectorRead read)
    {
        read.Interpolated = true;

        do
        {
            if (_scan[at].Kind == ScanTokenKind.Text)
            {
                read.Holes.Add(at);
            }

            at++;
        }
        while (at < end && Adjacent(at) && Joins(at));

        return at;
    }

    /// <summary>
    /// Whether the name that begins at <paramref name="at"/>, within <paramref name="end"/>, holds an interpolation: the
    /// token itself, or one joined to it in the run of names, interpolations, numbers and dashes a name is written in.
    /// </summary>
    private bool Interpolated(int at, int end)
    {
        var index = at;

        do
        {
            if (_scan[index].Kind == ScanTokenKind.Text)
            {
                return true;
            }

            index++;
        }
        while (index < end && Adjacent(index) && Joins(index));

        return false;
    }

    /// <summary>Whether the token at <paramref name="at"/> may be part of a name an interpolation writes: a name, an interpolation, a number or a dash.</summary>
    private bool Joins(int at) => _scan.IsName(at) || _scan[at].Kind is ScanTokenKind.Text or ScanTokenKind.Number || _scan.Is(at, "-");

    // ---- Declarations ----

    /// <summary>
    /// Whether the head with its first colon at <paramref name="colon"/> is a declaration: a property's name - a custom
    /// property's, a variable's, one an interpolation writes - before it, and where a block follows, one a declaration
    /// holds: a custom property's value, a Less detached ruleset, or SCSS's nested properties, a space after the colon -
    /// <c>a:hover {</c> is a selector.
    /// </summary>
    private bool IsDeclaration(Head head, int colon)
    {
        var at = head.Start;

        // An old Internet Explorer's hack, *zoom: 1, is a declaration of zoom.
        if (_scan.Is(at, "*") && at + 1 < colon && Adjacent(at + 1))
        {
            at++;
        }

        if (at >= colon)
        {
            return false;
        }

        for (var index = at; index < colon; index++)
        {
            if (!(_scan.IsName(index) || _scan[index].Kind == ScanTokenKind.Text || _scan.Is(index, "-")) || (index > at && !Adjacent(index)))
            {
                return false;
            }
        }

        if (!head.Opens)
        {
            return true;
        }

        var name = _scan.Text(at);
        return name.StartsWith("--", StringComparison.Ordinal) || name[0] == '@' || colon + 1 == head.Stop || !Adjacent(colon + 1);
    }

    /// <summary>
    /// A declaration whose colon is at <paramref name="colon"/>: a custom property's, a SCSS or Sass variable's and a Less
    /// variable's a constant over its lines; its value read for what it uses, from the constant it is given to or the node
    /// that holds it; the block a Less detached ruleset or SCSS's nested properties open read in its turn. Answers where
    /// the next statement begins.
    /// </summary>
    private int Declaration(Head head, int colon, int to, ScanFrame frame, IReadOnlyList<string> parents)
    {
        var at = _scan.Is(head.Start, "*") ? head.Start + 1 : head.Start;
        var name = _scan.IsName(at) && colon == at + 1 ? _scan.Text(at) : null;
        var custom = name is ['-', '-', ..];
        var end = custom ? ValueEnd(colon + 1, to) : head.Stop;
        var from = frame.Parent;

        if (name is ['-', '-', ..] or ['$', _, ..] or ['@', _, ..])
        {
            from = Node(name, CodeSubKinds.StyleVariable, head with { Stop = end, Opens = false }, _scan.Signature(at, end), exports: false);
        }

        // What an interpolation in the name uses, and what the value does; the name itself is declared, never used.
        Uses(at, colon, from, names: false);
        Uses(colon + 1, end, from);

        if (name is not null && Unvendored(name) is "animation" or "animation-name")
        {
            Animated(colon + 1, end, from);
        }

        if (name == "composes")
        {
            Composed(colon + 1, end, from);
        }

        if (custom)
        {
            return end < to && _scan.Is(end, ";") ? end + 1 : end;
        }

        if (head.Opens)
        {
            Block(head.Stop + 1, head.Close, name is ['@', ..] && from is { } ruleset ? frame.Inside(null, null, ruleset, null) : frame.Deeper(), parents, keyframes: false);
        }

        return head.Past;
    }

    /// <summary>
    /// An <c>animation</c>'s or an <c>animation-name</c>'s value, from the token at <paramref name="from"/> to
    /// <paramref name="to"/>: each name or string it writes a use of the keyframes of that name, made from
    /// <paramref name="by"/> - but a word the shorthand gives a meaning of its own (<see cref="AnimationWords"/>), a
    /// function's name and what its parentheses hold, and a variable, which <see cref="Uses"/> reads.
    /// </summary>
    private void Animated(int from, int to, string? by)
    {
        for (var at = from; at < to; at++)
        {
            if (_scan.Opens(at))
            {
                at = _scan.Bounds(at, to).Past - 1;
                continue;
            }

            var named = _scan.IsName(at) && _scan.Text(at) is var word && word[0] is not ('$' or '@') && !word.StartsWith("--", StringComparison.Ordinal)
                && !AnimationWords.Contains(word) && !(_scan.Is(at + 1, "(") && Adjacent(at + 1))
                ? CssLexer.Unescaped(word)
                : _scan[at].Kind == ScanTokenKind.Text && _scan.Text(at) is ['"' or '\'', ..] ? Unquoted(at) : null;

            if (named is { Length: > 0 })
            {
                _references.Add((by, CodeEdgeKinds.Uses, named, _scan[at].Line, new ExtractQualifier(CodeQualifierKinds.Keyframes)));
            }
        }
    }

    /// <summary>A CSS module's <c>composes: a b from "./x.css"</c>: a use of each class it composes, as markup's <c>class</c> is.</summary>
    private void Composed(int from, int to, string? by)
    {
        for (var at = from; at < to && !_scan.Is(at, "from"); at++)
        {
            if (_scan.IsName(at) && _scan.Text(at) is var composed && composed[0] is not ('$' or '@') && !composed.StartsWith("--", StringComparison.Ordinal))
            {
                _references.Add((by, CodeEdgeKinds.Uses, $".{CssLexer.Unescaped(composed)}", _scan[at].Line, MarkupDialect.Selector));
            }
        }
    }

    /// <summary>
    /// What the tokens from <paramref name="from"/> to <paramref name="to"/> use, made from <paramref name="by"/>: each
    /// custom property they name (<c>var(--x)</c>), each SCSS and Sass variable (<c>$x</c>, a module's <c>m.$x</c> through
    /// its namespace) and each Less one (<c>@x</c>); and the code of each interpolation they hold, read in its turn as a
    /// text of its own, no deeper than <see cref="ScanFrame.MostDepth"/>. Where <paramref name="names"/> is false, only
    /// the interpolations are read.
    /// </summary>
    private void Uses(int from, int to, string? by, bool names = true)
    {
        var pending = new Queue<(InterpolatedText Text, int From, int To, int Depth)>();
        pending.Enqueue((_text, from, to, 0));

        while (pending.TryDequeue(out var run))
        {
            var scan = run.Text.Scan;

            for (var at = Math.Max(run.From, 0); at < run.To && at < scan.Count; at++)
            {
                var token = scan[at];

                if (token.Kind == ScanTokenKind.Name && (names || run.Depth > 0))
                {
                    Used(scan, at, by);
                }
                else if (token.Kind == ScanTokenKind.Text && run.Text.Interpolations.TryGetValue(token.Start, out var spans) && ScanFrame.ReadsDeeper(_extract, run.Depth, token.Line, "interpolations"))
                {
                    foreach (var (start, end) in spans)
                    {
                        if (start >= 2 && _scan.Source[start - 2] == '@' && _scan.Source[start - 1] == '{')
                        {
                            // Less's @{name}: the variable it names.
                            if (_scan.Source[start..end].Trim() is { Length: > 0 } variable && variable.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
                            {
                                _references.Add((by, CodeEdgeKinds.Uses, $"@{variable}", token.Line, new ExtractQualifier(CodeQualifierKinds.StyleVariable)));
                            }

                            continue;
                        }

                        var inner = CssLexer.Scan(_scan.Source, start, end, _text.Lines, _syntax);
                        pending.Enqueue((inner, 0, inner.Scan.Count, run.Depth + 1));
                    }
                }
            }
        }
    }

    /// <summary>
    /// The use the name at <paramref name="at"/> of <paramref name="scan"/> is, made from <paramref name="by"/>: of a custom
    /// property or a variable, a variable's qualifier saying so, a module's variable through its namespace; none of any
    /// other name.
    /// </summary>
    private void Used(ScannedText scan, int at, string? by)
    {
        var name = scan.Text(at);

        if (name.Length < 2 || !(name.StartsWith("--", StringComparison.Ordinal) ? name.Length > 2 : name[0] is '$' or '@'))
        {
            return;
        }

        var qualifier = name[0] == '$' && at >= 2 && scan.Is(at - 1, ".") && scan[at - 1].End == scan[at].Start && scan.IsName(at - 2) && scan[at - 2].End == scan[at - 1].Start
            ? new ExtractQualifier(CodeQualifierKinds.Prefix, scan.Text(at - 2))
            : new ExtractQualifier(CodeQualifierKinds.StyleVariable);

        _references.Add((by, CodeEdgeKinds.Uses, name, scan[at].Line, qualifier));
    }

    // ---- Nodes and the text ----

    /// <summary>
    /// The key of the node of <paramref name="name"/>, a member of <paramref name="subKind"/> over <paramref name="head"/>'s
    /// lines - to its block's close - made where the sheet declares it first and kept where it already stands; exported by
    /// its name to the scripts that import the sheet, as a CSS module exports its classes, ids and keyframes, where
    /// <paramref name="exports"/> and the sheet is read as one.
    /// </summary>
    private string Node(string name, string subKind, Head head, string signature, bool exports)
    {
        if (_named.TryGetValue(name, out var known))
        {
            return known;
        }

        var start = LineOf(head.Start);
        var end = head.Opens && head.Close < _scan.Count && _scan.Is(head.Close, "}") && _scan.Match(head.Close) == head.Stop
            ? _scan[head.Close].Line
            : _scan[_scan.LastOf(head.Opens ? head.Close : head.Stop)].EndLine;

        var key = _extract.Add(new ExtractNode($"{_language}:{_path}#{name}", CodeNodeKinds.Member, name, name, start, Math.Max(start, end), signature.Length > 0 ? signature : ExtractText.Signature(name))
        {
            SubKind = subKind,
            Summary = ScanComments.Summary(_scan, head.Start),
            Exports = exports && _module ? Exported(name, subKind) : null,
        });

        _named[name] = key;
        _first ??= key;
        return key;
    }

    /// <summary>
    /// The names a CSS module exports <paramref name="name"/>, a node of <paramref name="subKind"/>, by: a class's and an
    /// id's without their marker, a keyframes' as it is - each as written and camel-cased, as a bundler's
    /// <c>camelCase</c> convention adds it; none of an element or a placeholder.
    /// </summary>
    private static IReadOnlyList<string>? Exported(string name, string subKind)
    {
        var bare = name[0] is '.' or '#' ? name[1..] : subKind == CodeSubKinds.Keyframes ? name : string.Empty;

        if (bare.Length == 0)
        {
            return null;
        }

        var camel = new StringBuilder(bare.Length);

        for (var at = 0; at < bare.Length; at++)
        {
            if (bare[at] is '-' or '_' && camel.Length > 0 && at + 1 < bare.Length && char.IsAsciiLetterOrDigit(bare[at + 1]))
            {
                camel.Append(char.ToUpperInvariant(bare[++at]));
            }
            else if (bare[at] is not ('-' or '_') || camel.Length == 0)
            {
                camel.Append(bare[at]);
            }
        }

        return camel.ToString() is var cased && cased != bare ? [bare, cased] : [bare];
    }

    /// <summary>The head's signature: its tokens on one line, cut to a signature's length.</summary>
    private string Signature(Head head) => _scan.Signature(head.Start, head.Stop);

    /// <summary>The parts of the tokens from <paramref name="from"/> to <paramref name="to"/> its commas part, outside its brackets.</summary>
    private List<(int Start, int End)> Split(int from, int to)
    {
        var parts = new List<(int Start, int End)>();
        var start = from;

        for (var at = from; at < to; at++)
        {
            if (_scan.Is(at, ","))
            {
                parts.Add((start, at));
                start = at + 1;
            }
            else if (_scan.Opens(at))
            {
                at = _scan.Bounds(at, to).Past - 1;
            }
        }

        parts.Add((start, to));
        return parts;
    }

    /// <summary>The name the token at <paramref name="at"/> writes, escapes read; <see langword="null"/> where an interpolation writes part of it.</summary>
    private string? Plain(int at) => Interpolated(at, _scan.Count) ? null : CssLexer.Unescaped(_scan.Text(at));

    /// <summary>The text of the string at <paramref name="at"/> between its quotes; <see langword="null"/> where it is none, is left open, holds an escape or an interpolation.</summary>
    private string? Unquoted(int at)
    {
        if (at >= _scan.Count || _scan[at].Kind != ScanTokenKind.Text || _text.Interpolations.ContainsKey(_scan[at].Start))
        {
            return null;
        }

        var text = _scan.Text(at);
        return text.Length >= 2 && text[0] is '"' or '\'' && text[^1] == text[0] && !text.Contains('\\', StringComparison.Ordinal) ? text[1..^1] : null;
    }

    /// <summary>The path an unquoted <c>url(...)</c> at <paramref name="at"/> holds; <see langword="null"/> where it is none, is left open, or holds an interpolation.</summary>
    private string? Url(int at)
    {
        if (_text.Interpolations.ContainsKey(_scan[at].Start))
        {
            return null;
        }

        var text = _scan.Text(at);
        return text.Length > 5 && text.StartsWith("url(", StringComparison.OrdinalIgnoreCase) && text[^1] == ')' && !text.Contains('\\', StringComparison.Ordinal) ? text[4..^1].Trim() : null;
    }

    /// <summary>Whether the token at <paramref name="at"/> follows the one before it with nothing between.</summary>
    private bool Adjacent(int at) => at > 0 && at < _scan.Count && _scan[at - 1].End == _scan[at].Start;

    /// <summary>The line the token at <paramref name="at"/> begins on; the text's last where it is past the last.</summary>
    private int LineOf(int at) => _scan.Count == 0 ? 1 : at < _scan.Count ? _scan[at].Line : _scan[_scan.Count - 1].EndLine;
}
