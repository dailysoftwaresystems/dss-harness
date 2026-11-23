using System.Text;
using DssHarness.Core.Codebase.Languages.Scanning;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>Whose a style sheet's names are: the page's every rule, or a module's or a component's own.</summary>
internal enum StyleScope
{
    /// <summary>A sheet every page it reaches is styled by: each of its nodes the repository's, reached by any file.</summary>
    Global,

    /// <summary>
    /// A CSS module - a sheet named <c>x.module.css</c>, a component's <c>&lt;style module&gt;</c>: each class, id and
    /// keyframes its own, but one written under <c>:global</c>, seen from its file alone and exported by its name, as a
    /// bundler renames them; its element rules, variables, mixins, functions and font faces the repository's.
    /// </summary>
    Module,

    /// <summary>
    /// A component's <c>&lt;style scoped&gt;</c>: the parts of each selector's last compound - the element the rule
    /// styles, which Vue's attribute marks - and its keyframes the component's own, seen from its file alone, but one
    /// written under <c>:deep</c>, <c>:slotted</c> or <c>:global</c>; the rest the repository's.
    /// </summary>
    Scoped,
}

/// <summary>
/// One style sheet's reading over the structural scanner's text (<see cref="CssLexer"/>): its statements walked block by
/// block, each block the scan's matched braces hold, each line a token's - with a stack of the blocks it is in, never a
/// call into itself, so a block nested to any depth is read and no nesting reaches past the reader's stack. A statement
/// is an at-rule, by its word; a declaration, a name and its colon,
/// whose value runs to its semicolon or its block's close; or a rule, a selector and the block it opens - a nested
/// rule read inside the block that holds it, each rule's lines its own to its own close. Every reference and every
/// literal is made from the innermost node that holds it: the rule whose block it is written in - the subject its
/// selector styles - the mixin, the function, the keyframes, or the variable its value is given to; what no node holds,
/// from the sheet's own top node, <c>(sheet)</c>, made the first time a reference needs it. A name a block declares of
/// its own - a mixin's or a function's parameter, a loop's variable, a Sass or Less variable a block declares without
/// <c>!global</c> - is that block's local: no node, and no reference of it.
/// </summary>
internal sealed class CssReading
{
    /// <summary>The kind of the marks the reading makes, the sign of a limit of the style sheets' help (<see cref="CodeGraphLimit.Signs"/>): what an interpolation writes.</summary>
    internal const string Interpolation = "interpolation";

    /// <summary>The name of a sheet's top node: what its top does outside every node, never a name a sheet or code writes.</summary>
    private const string SheetName = "(sheet)";

    /// <summary>
    /// The pseudo-classes whose arguments are selectors of the element itself, read as the selector's own parts: CSS's,
    /// a shadow tree's, a CSS module's <c>:global</c> and <c>:local</c>, and Vue's <c>:deep</c>, <c>::v-deep</c> and
    /// <c>:slotted</c>.
    /// </summary>
    private static readonly HashSet<string> Alternatives = new(StringComparer.OrdinalIgnoreCase)
    {
        "is", "where", "matches", "any", "-webkit-any", "-moz-any", "host", "slotted", "global", "local", "deep", "v-deep",
    };

    /// <summary>The pseudo-classes whose selectors are no module's or component's own (<see cref="StyleScope"/>): what they hold is the page's.</summary>
    private static readonly HashSet<string> Unscoped = new(StringComparer.OrdinalIgnoreCase) { "global", "deep", "v-deep", "slotted" };

    /// <summary>
    /// CSS's functions Sass reads as special ones - their arguments no Sass, and no <c>@function</c> named so - which a
    /// call of them names no function a sheet declares.
    /// </summary>
    private static readonly HashSet<string> SpecialFunctions = new(StringComparer.OrdinalIgnoreCase) { "calc", "clamp", "element", "env", "expression", "max", "min", "url", "var" };

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

    /// <summary>
    /// The families a <c>font-family</c> or a <c>font</c> names that no <c>@font-face</c> declares - CSS's generic ones and
    /// the values every property takes - and the <c>font</c> shorthand's system fonts.
    /// </summary>
    private static readonly HashSet<string> GenericFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        "serif", "sans-serif", "monospace", "cursive", "fantasy", "system-ui", "ui-serif", "ui-sans-serif", "ui-monospace",
        "ui-rounded", "emoji", "math", "fangsong", "inherit", "initial", "unset", "revert", "revert-layer", "caption", "icon",
        "menu", "message-box", "small-caption", "status-bar",
    };

    private readonly string _language;
    private readonly string _path;
    private readonly StyleSyntax _syntax;
    private readonly StyleScope _scope;
    private readonly QualifiedName _kept;
    private readonly InterpolatedText _text;
    private readonly ScannedText _scan;
    private readonly ScanExtract _extract = new();

    /// <summary>The key of each node, by its kind, its name and whether it is the module's or the component's own: one node of each in a sheet, where it is first declared.</summary>
    private readonly Dictionary<(string SubKind, string Name, bool Own), QualifiedName> _named = [];

    /// <summary>The references, in the order the text makes them, each from the key of the node that holds it - none where no node does.</summary>
    private readonly List<(QualifiedName? From, string Kind, string Name, int Line, ExtractQualifier? Qualifier)> _references = [];

    /// <summary>The key of the sheet's top node, once a reference needs it.</summary>
    private QualifiedName? _top;

    /// <summary>Whether the closed scope of the file's own - what a module's or a component's own nodes are kept to - is declared.</summary>
    private bool _keptDeclared;

    /// <summary>The fragment of each node's id, by its key: what a node a suffix is written onto it by spells its own with.</summary>
    private readonly Dictionary<QualifiedName, string> _fragments = new();

    /// <summary>Where the sheet's first scan found each interpolation closes: what the scan of each one's code passes the ones it holds over by.</summary>
    private readonly CssLexer.Closes _closes = new();

    /// <summary>How many of the blocks being read declare each name of their own (<see cref="Locals"/>).</summary>
    private readonly Dictionary<string, int> _held = new(StringComparer.Ordinal);

    /// <summary>The blocks being read, the innermost on top.</summary>
    private readonly Stack<Cursor> _blocks = new();

    /// <summary>The block the statement just read opens, to be read next; <see langword="null"/> where it opens none.</summary>
    private Cursor? _entered;

    /// <summary>Reads <paramref name="text"/>, the sheet at <paramref name="path"/>, as part of the extract of <paramref name="language"/>.</summary>
    /// <param name="language">The implementation the extract is part of: the prefix of every id.</param>
    /// <param name="path">The file's path.</param>
    /// <param name="text">The sheet's text, at the lines it stands on in its file.</param>
    /// <param name="syntax">What it is written in.</param>
    /// <param name="scope">Whose its names are: the page's, a module's, a scoped component's.</param>
    /// <param name="kept">
    /// The scope a module's or a component's own nodes are kept to, closed, which every declaration of the file is written
    /// in: the file's own where none is given; a module block's of its own, which a binding of the block names
    /// (<see cref="CodeQualifierKinds.Module"/>).
    /// </param>
    public CssReading(string language, string path, CodeText text, StyleSyntax syntax, StyleScope scope, QualifiedName? kept = null)
    {
        _language = language;
        _path = path;
        _syntax = syntax;
        _scope = scope;
        _kept = kept ?? QualifiedName.Of(CodeGraphIds.FileId(path));
        _text = CssLexer.Scan(syntax == StyleSyntax.Sass ? CssLexer.Braced(text) : text, syntax, _closes);
        _scan = _text.Scan;
    }

    /// <summary>The scope a module's or a component's own nodes are kept to, closed, which every declaration of the file is written in.</summary>
    private QualifiedName Kept => _kept;

    /// <summary>The sheet's extract.</summary>
    public FileExtract Read()
    {
        _extract.Generated = ScanComments.Generated(_scan);
        Walk();

        foreach (var (from, kind, name, line, qualifier) in _references)
        {
            _extract.Reference(from ?? Top(), kind, name, line, qualifier: qualifier);
        }

        return _extract.Build();
    }

    /// <summary>
    /// Where a statement stands: the frame its references are made from - its parent the node that holds them - the
    /// selectors a <c>&amp;</c> stands for, each by the part a suffix is written to, and the names the blocks it is in
    /// declare of their own.
    /// </summary>
    private sealed record Place(ScanFrame Frame, IReadOnlyList<Atom> Parents, Locals? Locals)
    {
        /// <summary>Whether it is a keyframes' block, whose selectors are no rules.</summary>
        public bool Keyframes { get; init; }

        /// <summary>Whether it is a <c>@font-face</c>'s block, whose <c>font-family</c> names the face itself.</summary>
        public bool Face { get; init; }

        /// <summary>The key of the node a reference made here is made from; <see langword="null"/> at the sheet's top.</summary>
        public QualifiedName? From => Frame.Parent;
    }

    /// <summary>
    /// The names one block declares of its own: a flow control's block - an <c>@if</c>'s, an <c>@each</c>'s - declares in
    /// the block that holds it, as Sass reads one, but for the variables a loop binds. What the blocks being read declare
    /// is counted in one table, each name by how many of them declare it, so whether one is a block's own is known at once,
    /// never by walking out through the blocks it is in; a block closed takes its names away.
    /// </summary>
    private sealed class Locals
    {
        private readonly Dictionary<string, int> _held;

        /// <summary>The block a name it declares is put in: itself, or for a flow control's the one that holds it; none at the sheet's top.</summary>
        private readonly Locals? _owner;

        private List<string>? _names;

        /// <param name="outer">The block that holds it; <see langword="null"/> at the sheet's top.</param>
        /// <param name="flow">Whether it is a flow control's block.</param>
        /// <param name="held">How many of the blocks being read declare each name.</param>
        public Locals(Locals? outer, bool flow, Dictionary<string, int> held)
        {
            _held = held;
            _owner = flow ? outer?._owner : this;
        }

        /// <summary>A name this block binds itself: a parameter, a loop's variable.</summary>
        public void Bind(string name) => Own(name);

        /// <summary>Declares <paramref name="name"/> in the nearest block that is no flow control's; false where none is and the name is the sheet's.</summary>
        public bool Declare(string name)
        {
            _owner?.Own(name);
            return _owner is not null;
        }

        /// <summary>Whether a block being read declares <paramref name="name"/>: this one or one it is in, as only those are open.</summary>
        public bool Holds(string name) => _held.GetValueOrDefault(name) > 0;

        /// <summary>The block is read: its names are no one's.</summary>
        public void Close()
        {
            foreach (var name in _names ?? [])
            {
                _held[name]--;
            }

            _names = null;
        }

        private void Own(string name)
        {
            (_names ??= []).Add(name);
            _held[name] = _held.GetValueOrDefault(name) + 1;
        }
    }

    /// <summary>A block being read: the index of the statement it goes on at, the index past its last, and where its statements stand.</summary>
    private sealed class Cursor(int at, int to, Place place)
    {
        public int At { get; set; } = at;

        public int To => to;

        public Place Place => place;
    }

    /// <summary>
    /// The sheet's statements, block by block in the order they are written: a block a statement opens read next, its own
    /// read before the statements after the one that opened it - each statement read once, whichever block it stands in.
    /// </summary>
    private void Walk()
    {
        _blocks.Push(new Cursor(0, _scan.Count, new Place(ScanFrame.Top, [], null)));

        while (_blocks.TryPeek(out var block))
        {
            if (block.At >= block.To)
            {
                _blocks.Pop();
                block.Place.Locals?.Close();
                continue;
            }

            var at = block.At;
            var next = Statement(at, block.To, block.Place);
            block.At = next > at ? next : at + 1;

            if (_entered is { } entered)
            {
                _entered = null;
                _blocks.Push(entered);
            }
        }
    }

    /// <summary>
    /// The block <paramref name="head"/> opens, read as a block of its own in <paramref name="frame"/> once the statement
    /// that opens it is read - the last a statement reads, and one block a statement; nothing where it opens none.
    /// </summary>
    private void Body(Head head, Place place, ScanFrame frame, IReadOnlyList<Atom> parents, bool flow = false, IEnumerable<string>? binds = null, bool keyframes = false, bool face = false)
    {
        if (!head.Opens)
        {
            return;
        }

        var locals = new Locals(place.Locals, flow, _held);

        foreach (var name in binds ?? [])
        {
            locals.Bind(name);
        }

        _entered = new Cursor(head.Stop + 1, head.Close, new Place(frame, parents, locals) { Keyframes = keyframes, Face = face });
    }

    /// <summary>The statement that begins at <paramref name="at"/>, within <paramref name="to"/>; answers where the next begins.</summary>
    private int Statement(int at, int to, Place place)
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
            At(word, head, place);
            return past;
        }

        if (Colon(at, stop) is var colon and >= 0 && IsDeclaration(head, colon))
        {
            return Declaration(head, colon, to, place);
        }

        if (!opens)
        {
            Called(head, place);
            return past;
        }

        if (place.Keyframes)
        {
            // A keyframe's selector - from, to, a percentage - is no rule: its declarations are the keyframes'.
            Uses(head.Start, head.Stop, place.From, place.Locals, names: false);
            Body(head, place, place.Frame, place.Parents, keyframes: true);
            return past;
        }

        Rule(head, head.Start, place);
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
    private void At(string word, Head head, Place place)
    {
        var at = head.Start;
        var line = LineOf(at);
        var after = at + 1;
        var frame = place.Frame;

        switch (word)
        {
            case "import":
                Imports(head, after, head.Stop, word, place, prefix: null, export: false, show: null, hide: null);
                return;

            case "use":
                Use(head, after, place);
                return;

            case "forward":
                Forward(head, after, place);
                return;

            case "mixin" or "function":
                if (_scan.IsName(after) && Plain(after) is { } routine)
                {
                    var key = Node(routine, word == "mixin" ? CodeSubKinds.StyleMixin : CodeSubKinds.StyleFunction, head, Signature(head), own: false);
                    var parameters = Parameters(after + 1, head.Stop, key, place.Locals);
                    Body(head, place, frame.Inside(null, null, key, null), [], binds: parameters);
                }
                else
                {
                    Uses(after, head.Stop, place.From, place.Locals, names: false);
                    Body(head, place, frame, place.Parents);
                }

                return;

            case "include":
                Body(head, place, frame, place.Parents, binds: Included(head, after, place));
                return;

            case "extend":
                // Sass refuses a & in what an @extend names: it stands for nothing there.
                foreach (var part in Selector(after, head.Stop, [], global: false).Parts)
                {
                    _references.Add((place.From, CodeEdgeKinds.Inherits, part.Item.Name!, line, MarkupDialect.Selector));
                }

                Uses(after, head.Stop, place.From, place.Locals, names: false);
                return;

            case "apply":
                Applied(head, after, place);
                return;

            case "keyframes":
                if (Named(after, head.Stop) is { } animation)
                {
                    var key = Node(animation, CodeSubKinds.Keyframes, head, Signature(head), own: _scope != StyleScope.Global);
                    Uses(after, head.Stop, key, place.Locals, names: false);
                    Body(head, place, frame.Inside(null, null, key, null), [], keyframes: true);
                }
                else
                {
                    Uses(after, head.Stop, place.From, place.Locals, names: false);
                    Body(head, place, frame, [], keyframes: true);
                }

                return;

            case "font-face":
                if (head.Opens && FontFamily(head.Stop + 1, head.Close) is (string family, int start, int end))
                {
                    var key = Node(Folded(family), CodeSubKinds.FontFace, head, _scan.Signature(start, end), own: false);
                    Body(head, place, frame.Inside(null, null, key, null), place.Parents, face: true);
                }
                else
                {
                    Body(head, place, frame, place.Parents, face: true);
                }

                return;

            case "property":
                if (_scan.IsName(after) && _scan.Text(after) is ['-', '-', ..] property)
                {
                    var key = Node(property, CodeSubKinds.StyleVariable, head, Signature(head), own: false);
                    Body(head, place, frame.Inside(null, null, key, null), place.Parents);
                }
                else
                {
                    Body(head, place, frame, place.Parents);
                }

                return;

            case "at-root" when after < head.Stop && !_scan.Is(after, "(") && head.Opens:
                // A rule written at the sheet's root from inside another: its selector is the rest of the head.
                Rule(head, after, place);
                return;

            case "each" or "for":
                // The variables a loop binds are its own: what it uses is past its in or its from.
                var binds = after;
                var bound = new List<string>();

                for (; binds < head.Stop && !_scan.Is(binds, "in") && !_scan.Is(binds, "from"); binds++)
                {
                    if (_scan.IsName(binds) && _scan.Text(binds) is ['$', _, ..] variable)
                    {
                        bound.Add(variable);
                    }
                }

                Uses(binds, head.Stop, place.From, place.Locals);
                Body(head, place, frame, place.Parents, flow: true, binds: bound);
                return;

            case "if" or "else" or "while":
                Uses(after, head.Stop, place.From, place.Locals);
                Body(head, place, frame, place.Parents, flow: true);
                return;

            default:
                // A conditional group - @media, @supports, @container, @layer - and every other: its head read for what it
                // uses, its block read as the block that holds it reads one.
                Uses(after, head.Stop, place.From, place.Locals);
                Body(head, place, frame, place.Parents);
                return;
        }
    }

    /// <summary>
    /// The parameters of a <c>@mixin</c>, a <c>@function</c> or an <c>@include</c>'s <c>using</c>, from the token at
    /// <paramref name="at"/>: each one's name the block's own, and what each default uses - a parameter before it among
    /// them, which is the block's own too - made from <paramref name="by"/>. Answers the names.
    /// </summary>
    private List<string> Parameters(int at, int stop, QualifiedName? by, Locals? locals)
    {
        if (!_scan.Is(at, "("))
        {
            Uses(at, stop, by, locals);
            return [];
        }

        var (inner, past) = _scan.Bounds(at, stop);
        var named = new Locals(locals, flow: false, _held);
        var names = new List<string>();

        foreach (var (start, end) in Split(at + 1, inner))
        {
            if (start < end && _scan.IsName(start) && _scan.Text(start) is ['$' or '@', _, ..] parameter)
            {
                names.Add(parameter);
                named.Bind(parameter);
            }
        }

        Uses(at + 1, inner, by, named);
        named.Close();
        Uses(past, stop, by, locals);
        return names;
    }

    /// <summary>
    /// An <c>@include</c>: a use of the mixin it names - through the module its namespace names, where it writes one - and
    /// of what its arguments use. Answers the variables its <c>using</c> binds, which are its content block's own.
    /// </summary>
    private List<string> Included(Head head, int at, Place place)
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

        var binds = stop < head.Stop ? Parameters(stop + 1, head.Stop, place.From, place.Locals) : [];

        if (!_scan.IsName(at))
        {
            Uses(at, stop, place.From, place.Locals);
            return binds;
        }

        var line = LineOf(at);

        if (_scan.Is(at + 1, ".") && Adjacent(at + 1) && _scan.IsName(at + 2) && Adjacent(at + 2))
        {
            _references.Add((place.From, CodeEdgeKinds.Uses, _scan.Text(at + 2), line, new ExtractQualifier(CodeQualifierKinds.Prefix, _scan.Text(at))));
            Uses(at + 3, stop, place.From, place.Locals);
            return binds;
        }

        _references.Add((place.From, CodeEdgeKinds.Uses, _scan.Text(at), line, new ExtractQualifier(CodeQualifierKinds.StyleMixin)));
        Uses(at + 1, stop, place.From, place.Locals);
        return binds;
    }

    /// <summary>
    /// Tailwind's <c>@apply</c>: each class it applies a use of the rule of that name - the class past a variant's
    /// <c>hover:</c>, an <c>!important</c>'s mark left out - as markup's <c>class</c> is.
    /// </summary>
    private void Applied(Head head, int at, Place place)
    {
        if (at >= head.Stop)
        {
            return;
        }

        var line = LineOf(at);
        var written = _scan.Source.Slice(_scan[at].Start, _scan[head.Stop - 1].End);

        foreach (var word in written.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var applied = Variantless(word).Trim('!');

            if (applied.Length > 0 && applied != "important" && !applied.Contains("#{", StringComparison.Ordinal) && !applied.Contains("@{", StringComparison.Ordinal))
            {
                _references.Add((place.From, CodeEdgeKinds.Uses, $".{applied}", line, MarkupDialect.Selector));
            }
        }

        Uses(at, head.Stop, place.From, place.Locals);
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

    /// <summary>
    /// The name a <c>@keyframes</c> gives its animation: a name, or a string's value; <see langword="null"/> where an
    /// interpolation writes it, said as dynamic, or it gives none.
    /// </summary>
    private string? Named(int at, int stop)
    {
        if (at >= stop)
        {
            return null;
        }

        if (_scan[at].Kind == ScanTokenKind.Text && _scan.Text(at) is ['"' or '\'', ..])
        {
            if (_scan.Value(at) is { Length: > 0 } quoted)
            {
                return quoted;
            }
        }
        else if (!Interpolated(at, stop))
        {
            return _scan.IsName(at) ? CssLexer.Unescaped(_scan.Text(at)) : null;
        }

        _extract.Dynamic(LineOf(at), "a name an interpolation writes, whose node is not read", Interpolation);
        return null;
    }

    /// <summary>
    /// The family a <c>@font-face</c>'s block names in its <c>font-family</c>, from the token at <paramref name="from"/> to
    /// <paramref name="to"/>, with where that declaration begins and ends; <see langword="null"/> where it names none.
    /// </summary>
    private (string Family, int Start, int End)? FontFamily(int from, int to)
    {
        for (var at = from; at + 1 < to; at++)
        {
            if (_scan.IsName(at) && string.Equals(_scan.Text(at), "font-family", StringComparison.OrdinalIgnoreCase) && _scan.Is(at + 1, ":"))
            {
                var end = ValueEnd(at + 2, to);
                return Family(at + 2, end) is { } family ? (family, at, end) : null;
            }

            if (_scan.Opens(at))
            {
                at = _scan.Bounds(at, to).Past - 1;
            }
        }

        return null;
    }

    /// <summary>
    /// The family the tokens from <paramref name="from"/> to <paramref name="to"/> name, as CSS reads one: a string's value,
    /// or a run of names joined by one space; <see langword="null"/> where they write anything else - a variable, a
    /// function, an interpolation - or nothing.
    /// </summary>
    private string? Family(int from, int to)
    {
        // An !important's mark is no part of the family.
        for (var at = from; at < to; at++)
        {
            if (_scan.Is(at, "!"))
            {
                to = at;
                break;
            }
        }

        if (to - from == 1 && _scan[from].Kind == ScanTokenKind.Text)
        {
            return _scan.Value(from) is { Length: > 0 } quoted ? quoted : null;
        }

        var words = new List<string>();

        for (var at = from; at < to; at++)
        {
            if (!_scan.IsName(at) || _scan.Text(at) is ['$' or '@', ..] or ['-', '-', ..] || (_scan.Is(at + 1, "(") && Adjacent(at + 1) && at + 1 < to) || Interpolated(at, to))
            {
                return null;
            }

            words.Add(CssLexer.Unescaped(_scan.Text(at)));
        }

        return words.Count > 0 ? string.Join(' ', words) : null;
    }

    /// <summary>A family's name as CSS matches one, its ASCII letters in lower case: what a <c>@font-face</c>'s node is named by, and what a font names it by.</summary>
    private static string Folded(string family)
        => string.Create(family.Length, family, static (folded, written) =>
        {
            for (var at = 0; at < written.Length; at++)
            {
                folded[at] = char.IsAsciiLetterUpper(written[at]) ? (char)(written[at] | 0x20) : written[at];
            }
        });

    // ---- Imports ----

    /// <summary>
    /// The files an <c>@import</c>, a <c>@use</c> or a <c>@forward</c> from the token at <paramref name="from"/> to
    /// <paramref name="to"/> names, each of its comma's parts one: an import of each file it may be - the first the tree
    /// holds taken - by <see cref="Candidates"/>; none of a package, a URL, a host's path or a module the language builds
    /// in; none where an interpolation writes the path, said as dynamic. What each part writes besides its path - a media
    /// query, a Less option, the interpolation a path holds - is read for what it uses.
    /// </summary>
    private void Imports(Head head, int from, int to, string word, Place place, string? prefix, bool export, IReadOnlyList<string>? show, IReadOnlyList<string>? hide)
    {
        foreach (var (start, end) in Split(from, to))
        {
            var (token, past, target) = Target(start, end);

            if (token < 0)
            {
                Uses(start, end, place.From, place.Locals);
                continue;
            }

            Uses(start, token, place.From, place.Locals);
            Uses(past, end, place.From, place.Locals);

            if (target is null)
            {
                _extract.Dynamic(LineOf(token), "an import whose path an interpolation writes, which is no import", Interpolation);
                Uses(token, past, place.From, place.Locals, names: false);
                continue;
            }

            // Sass compiles an import of a url(...) or one a media query follows to CSS's own, and loads no sheet for it.
            var plain = word == "import" && (_scan.Is(token, "url") || _scan.Text(token).StartsWith("url(", StringComparison.OrdinalIgnoreCase) || past < end);
            var line = LineOf(head.Start);
            var fallback = false;

            foreach (var candidate in Candidates(target, word, plain))
            {
                _extract.Import(new ExtractImport(CodeImportForms.Relative, candidate, line) { Fallback = fallback, Prefix = prefix, Export = export, Show = show, Hide = hide });
                fallback = true;
            }
        }
    }

    /// <summary>
    /// A <c>@use</c>: an import of its module by its namespace - the name its <c>as</c> gives, its file's own name where none
    /// does, none after <c>as *</c> - and what its <c>with</c> gives the module's variables read for what it uses.
    /// </summary>
    private void Use(Head head, int from, Place place)
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

        var (token, _, target) = Target(from, end);
        var prefix = target is not null ? Namespace(target) : null;

        if (token >= 0 && end < head.Stop && _scan.Is(end, "as"))
        {
            prefix = _scan.Is(end + 1, "*") ? null : _scan.IsName(end + 1) ? _scan.Text(end + 1) : prefix;
        }

        Imports(head, from, end, "use", place, prefix, export: false, show: null, hide: null);
        Configured(end, head.Stop, place);
    }

    /// <summary>A <c>@forward</c>: an export of what its module makes visible, its <c>show</c> and <c>hide</c> lists its filters, and what its <c>with</c> gives read for what it uses.</summary>
    private void Forward(Head head, int from, Place place)
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

        Imports(head, from, end, "forward", place, prefix: null, export: true, show, hide);
        Configured(end, head.Stop, place);
    }

    /// <summary>What the <c>with (...)</c> from the token at <paramref name="from"/> gives a module's variables, read for what it uses: each name it configures the module's, no use.</summary>
    private void Configured(int from, int to, Place place)
    {
        for (var at = from; at < to; at++)
        {
            if (_scan.Is(at, "with"))
            {
                Uses(at + 1, to, place.From, place.Locals);
                return;
            }
        }
    }

    /// <summary>
    /// The path the part of an import's head from <paramref name="start"/> to <paramref name="end"/> names, and the tokens
    /// that write it: its first string or <c>url(...)</c> - past Less's options in brackets - or, in the indented syntax,
    /// the path it writes unquoted, the rest of the part; a first token of -1 where it names none, and no path where an
    /// interpolation writes it.
    /// </summary>
    private (int Token, int Past, string? Path) Target(int start, int end)
    {
        if (start < end && _scan.Is(start, "("))
        {
            start = _scan.Bounds(start, end).Past;
        }

        if (start >= end)
        {
            return (-1, -1, null);
        }

        if (_scan[start].Kind == ScanTokenKind.Text)
        {
            return (start, start + 1, _scan.Value(start));
        }

        if (_scan.Is(start, "url") && _scan.Is(start + 1, "(") && start + 2 < end && _scan[start + 2].Kind == ScanTokenKind.Text)
        {
            return (start, _scan.Bounds(start + 1, end).Past, _scan.Value(start + 2));
        }

        if (_syntax == StyleSyntax.Sass)
        {
            return (start, end, Interpolated(start, end) ? null : _scan.Source.Slice(_scan[start].Start, _scan[end - 1].End).Trim());
        }

        return (-1, -1, null);
    }

    /// <summary>
    /// The files <paramref name="target"/> may be, in the order they are tried, as the sheet's language finds an
    /// <c>@import</c>, a <c>@use</c> or a <c>@forward</c> (<paramref name="word"/>) of it: a path with its own extension as
    /// written; with none, CSS's as written, Less's with <c>.less</c>, and Sass's by its load-path rule - each of Sass's
    /// extensions, the sheet's own first, then <c>.css</c>, each before its partial's <c>_name</c>; an <c>@import</c>'s
    /// <c>.import</c> file first; then the directory's <c>index</c> and <c>_index</c>. A Sass import Sass leaves to CSS
    /// (<paramref name="plain"/>) as written. None for a URL, a host's or a package's path, or a module Sass builds in
    /// (<c>sass:math</c>).
    /// </summary>
    private IReadOnlyList<string> Candidates(string target, string word, bool plain)
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

        if (plain)
        {
            return [target];
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
    /// over the rule's lines - <paramref name="head"/>'s, to its block's own close - a module's or a component's own where
    /// its scope says (<see cref="StyleScope"/>); what the head's interpolations and strings use and hold read - and in
    /// Less what a mixin's parameters' defaults and its guard use, the parameters the block's own; its block read with the
    /// rule's subject the node its references are made from, and what its <c>&amp;</c> stands for the nodes of the parts
    /// its selectors end with.
    /// </summary>
    private void Rule(Head head, int selector, Place place)
    {
        var read = Selector(selector, head.Stop, place.Parents, global: false);
        var signature = Signature(head);
        var keys = new QualifiedName[read.Parts.Count];
        QualifiedName? subject = null;
        QualifiedName? first = null;

        for (var at = 0; at < read.Parts.Count; at++)
        {
            var part = read.Parts[at];
            var own = _scope switch
            {
                StyleScope.Module => !part.Global && part.Item.Classy,
                StyleScope.Scoped => !part.Global && part.Last,
                _ => false,
            };

            var key = keys[at] = Node(part.Item, head, signature, own);
            first ??= key;

            if (part.Subject)
            {
                subject ??= key;
            }
        }

        subject ??= first ?? place.From;

        foreach (var extended in read.Extended)
        {
            _references.Add((subject, CodeEdgeKinds.Inherits, extended, LineOf(head.Start), MarkupDialect.Selector));
        }

        if (read.Interpolated)
        {
            _extract.Dynamic(LineOf(selector), "a selector an interpolation writes, whose rule is no node", Interpolation);
        }

        var parameters = _syntax == StyleSyntax.Less ? LessParameters(selector, head.Stop) : [];
        var locals = new Locals(place.Locals, flow: false, _held);

        foreach (var parameter in parameters)
        {
            locals.Bind(parameter);
        }

        Uses(selector, head.Stop, subject, locals, names: _syntax == StyleSyntax.Less);
        locals.Close();

        IReadOnlyList<Atom> parents = [.. read.Lasts.Select(tail => tail.Atom ?? AtomOf(read.Parts[tail.Part].Item, keys[tail.Part])).DistinctBy(atom => atom.Key)];
        Body(head, place, place.Frame.Inside(null, null, subject, null), parents, binds: parameters);
    }

    /// <summary>The selector the part <paramref name="item"/> of a rule, of the node <paramref name="key"/>, is to a <c>&amp;</c> nested in it.</summary>
    private static Atom AtomOf(Simple item, QualifiedName key) => new(key, item.Name, item.Classy, item.Dashed);

    /// <summary>The parameters a Less mixin's selector declares, from the token at <paramref name="from"/> to <paramref name="to"/>: each <c>@name</c> that begins a part of a group a class or an id opens.</summary>
    private List<string> LessParameters(int from, int to)
    {
        var names = new List<string>();

        for (var at = from; at < to; at++)
        {
            if (!_scan.Is(at, "(") || !Adjacent(at) || !_scan.IsName(at - 1))
            {
                continue;
            }

            var (inner, past) = _scan.Bounds(at, to);

            for (var index = at + 1; index < inner; index++)
            {
                if (_scan.IsName(index) && _scan.Text(index) is ['@', _, ..] parameter && (index == at + 1 || _scan.Is(index - 1, ",") || _scan.Is(index - 1, ";")))
                {
                    names.Add(parameter);
                }
            }

            at = past - 1;
        }

        return names;
    }

    /// <summary>
    /// A statement no block follows that is neither an at-rule nor a declaration: Less's call of a mixin (<c>.m();</c>,
    /// <c>#ns &gt; .m;</c>) a use of the rule it names, its <c>&amp;:extend(.b);</c> an inheritance of the rule it names;
    /// whatever else, read for what it uses. A <c>&amp;</c> a call writes a suffix to calls no mixin, as Less reads one.
    /// </summary>
    private void Called(Head head, Place place)
    {
        if (_syntax == StyleSyntax.Less && head.Start < head.Stop && (_scan.Is(head.Start, ".") || _scan.Is(head.Start, "#") || _scan.Is(head.Start, "&")))
        {
            var read = Selector(head.Start, head.Stop, [], global: false);
            var line = LineOf(head.Start);

            foreach (var extended in read.Extended)
            {
                _references.Add((place.From, CodeEdgeKinds.Inherits, extended, line, MarkupDialect.Selector));
            }

            if (read.Extended.Count == 0 && read.Parts.Count > 0 && read.Parts[^1].Item.Name is { } called)
            {
                _references.Add((place.From, CodeEdgeKinds.Uses, called, line, MarkupDialect.Selector));
            }
        }

        Uses(head.Start, head.Stop, place.From, place.Locals);
    }

    /// <summary>
    /// One selector a nested rule's <c>&amp;</c> stands for: the key of its node, its name - none where the node stands for
    /// names written onto others' (<see cref="ExtractNode.SuffixOf"/>) - whether the names it stands for are classes' and
    /// ids', and whether they end in a dash or an underscore after their first character, which a CSS module camel-cases
    /// with the suffix a <c>&amp;</c> writes after it (<see cref="CamelCased"/>).
    /// </summary>
    private sealed record Atom(QualifiedName Key, string? Name, bool Classy, bool Dashed);

    /// <summary>
    /// What one simple selector writes: a name of its own (<paramref name="Name"/>); or, where it writes a suffix onto a
    /// <c>&amp;</c> that stands for more than one selector, that suffix written onto each of <paramref name="Onto"/>, which
    /// one node stands for (<see cref="ExtractNode.SuffixOf"/>) - never their names one by one, so a list nested in a list
    /// is as many nodes as the sheet writes. <paramref name="Key"/> tells each apart in a selector and a sheet.
    /// </summary>
    private sealed record Simple(string Key, string? Name, string? Suffix, IReadOnlyList<Atom>? Onto, bool Classy, bool Dashed)
    {
        /// <summary>A name of its own: a class's, an id's, an element's, a placeholder's.</summary>
        public static Simple Named(string name) => new(name, name, null, null, name is ['.' or '#', ..], IsDashed(name));

        /// <summary><paramref name="suffix"/> written onto each of <paramref name="onto"/>.</summary>
        public static Simple Written(string suffix, IReadOnlyList<Atom> onto)
            => new($"&{suffix}\u0000{string.Join('\u0000', onto.Select(atom => atom.Key.Whole()))}", null, suffix, onto, onto.All(atom => atom.Classy), suffix[^1] is '-' or '_');

        /// <summary>Whether <paramref name="name"/> ends in a dash or an underscore after the first character of what a module exports it by.</summary>
        private static bool IsDashed(string name)
            => (name is ['.' or '#', ..] ? name.Length - 1 : name.Length) >= 2 && name[^1] is '-' or '_';
    }

    /// <summary>
    /// One simple part of a selector: what it writes; whether it is its subject's - the first part of the last compound
    /// that has one - whether it stands in a complex selector's last compound - the element the rule styles - and whether
    /// it is written under a pseudo-class whose selectors are the page's (<see cref="Unscoped"/>), in every place it is
    /// written.
    /// </summary>
    private sealed record Part(Simple Item, bool Subject, bool Last, bool Global);

    /// <summary>One selector a nested rule's <c>&amp;</c> stands for: a part of this one, by its index, or one the <c>&amp;</c> of this one stood for.</summary>
    private readonly record struct Tail(int Part, Atom? Atom);

    /// <summary>
    /// What a selector holds: its simple parts, in order, each once; what a nested rule's <c>&amp;</c> stands for - the
    /// parts each of its complex selectors ends with, or what its own <c>&amp;</c> stands for where one ends with it alone -
    /// each once; what a Less <c>:extend()</c> names; and whether an interpolation writes a part.
    /// </summary>
    private sealed class SelectorRead
    {
        private readonly HashSet<QualifiedName> _lasts = new();

        public List<Part> Parts { get; } = [];

        /// <summary>The index of each part among <see cref="Parts"/>, by its key.</summary>
        public Dictionary<string, int> Indices { get; } = new(StringComparer.Ordinal);

        public List<Tail> Lasts { get; } = [];

        public List<string> Extended { get; } = [];

        /// <summary>Whether an interpolation writes a part.</summary>
        public bool Interpolated { get; set; }

        /// <summary>Whether a complex selector of it gave its subject.</summary>
        public bool Subjected { get; set; }

        /// <summary>Adds <paramref name="tails"/> to what a nested rule's <c>&amp;</c> stands for, each once.</summary>
        public void Last(IEnumerable<Tail> tails)
        {
            foreach (var tail in tails)
            {
                if (_lasts.Add(tail.Atom is { } atom ? atom.Key : QualifiedName.Of(Parts[tail.Part].Item.Key)))
                {
                    Lasts.Add(tail);
                }
            }
        }
    }

    /// <summary>
    /// The compound selector a complex one is being read at: whether it has a part of its own yet, the parts its last
    /// simple selector writes, whether a <c>&amp;</c> stands for the parents in it, and the index of each part it writes.
    /// </summary>
    private sealed class Compound
    {
        public bool Empty { get; private set; } = true;

        public IReadOnlyList<int> Tail { get; private set; } = [];

        public bool Parent { get; set; }

        public List<int> Parts { get; private set; } = [];

        /// <summary>A compound begins: nothing of it read yet.</summary>
        public void Reset() => (Empty, Tail, Parent, Parts) = (true, [], false, []);

        /// <summary>Its last simple selector writes the parts of <paramref name="indices"/>.</summary>
        public void Wrote(IReadOnlyList<int> indices) => (Empty, Tail) = (false, indices);
    }

    /// <summary>Where what a pseudo-class's selectors hold goes: into the compound it is written in, into what a Less <c>:extend()</c> names, or nowhere.</summary>
    private enum Gathered
    {
        Compound,
        Extended,
        Nothing,
    }

    /// <summary>
    /// The selectors a pseudo-class's parentheses hold, being read: where what they hold goes, where they end and the
    /// selector goes on, what they hold so far - the outermost's own, the groups nested in it adding theirs - whether
    /// each is the page's to begin with, and, of the complex selector being read, where it begins and whether what it
    /// writes now is the page's.
    /// </summary>
    private sealed class Group(Gathered into, int end, int past, bool global, List<(Simple Item, bool Global)> held, bool owner)
    {
        public Gathered Into => into;

        public int End => end;

        public int Past => past;

        public bool Base { get; } = global;

        public List<(Simple Item, bool Global)> Held => held;

        /// <summary>Whether it is the outermost of the groups whose parts go where it says: the one that puts them there once it ends.</summary>
        public bool Owner => owner;

        public int Start { get; set; }

        public bool Global { get; set; } = global;
    }

    /// <summary>
    /// The selector list from the token at <paramref name="from"/> to <paramref name="to"/>, its parts the page's where
    /// <paramref name="global"/>, a <c>&amp;</c> in it standing for <paramref name="parents"/>: each complex selector of
    /// it read in turn, and the selectors an <c>:is()</c>, a <c>:where()</c>, a <c>:global()</c> and the like hold - nested
    /// to any depth - read as they stand with a stack of the groups the reading is in, never a call into itself, so no
    /// nesting reaches past the reader's stack, each token read once.
    /// </summary>
    private SelectorRead Selector(int from, int to, IReadOnlyList<Atom> parents, bool global)
    {
        var read = new SelectorRead();
        var groups = new Stack<Group>();
        var compound = new Compound();
        var subject = -1;
        IReadOnlyList<int> last = [];
        var start = from;
        var current = global;
        var at = from;

        // A compound ends at a space or a combinator: the last that has a part is the element the rule styles.
        void Reset()
        {
            if (compound.Parts.Count > 0)
            {
                last = compound.Parts;
            }

            compound.Reset();
        }

        // The complex selector ends: its last compound's parts are the element's, and what it ends with what a nested
        // rule's & stands for - its last simple selector's parts, or the parents where its last compound is the & alone.
        void Finish()
        {
            if (compound.Parts.Count > 0)
            {
                last = compound.Parts;
            }

            foreach (var index in last)
            {
                read.Parts[index] = read.Parts[index] with { Last = true };
            }

            read.Last(compound.Tail.Count > 0 ? compound.Tail.Select(index => new Tail(index, null)) : compound.Parent ? parents.Select(atom => new Tail(-1, atom)) : []);

            if (subject >= 0 && !read.Subjected)
            {
                read.Parts[subject] = read.Parts[subject] with { Subject = true };
                read.Subjected = true;
            }

            (compound, subject, last) = (new Compound(), -1, []);
        }

        // What a simple selector writes: the selector's own parts at its top, a group's held till the group ends.
        void Write(IReadOnlyList<Simple> items)
        {
            if (groups.TryPeek(out var group))
            {
                if (group.Into != Gathered.Nothing)
                {
                    group.Held.AddRange(items.Select(item => (item, group.Global)));
                }

                return;
            }

            Add(read, compound, ref subject, items, [.. items.Select(_ => current)]);
        }

        while (true)
        {
            var group = groups.TryPeek(out var top) ? top : null;
            var end = group?.End ?? to;

            if (at >= end)
            {
                if (group is null)
                {
                    Finish();
                    break;
                }

                groups.Pop();

                if (group.Owner)
                {
                    Gather(read, compound, ref subject, group);
                }

                at = group.Past;
                continue;
            }

            if (_scan.Is(at, ","))
            {
                // A complex selector of the list ends, and the next begins.
                if (group is null)
                {
                    Finish();
                    (start, current) = (at + 1, global);
                }
                else
                {
                    (group.Start, group.Global) = (at + 1, group.Base);
                }

                at++;
                continue;
            }

            var begins = group?.Start ?? start;

            if (group is null && at > begins && !Adjacent(at))
            {
                Reset();
            }

            if (_scan.Is(at, ">") || _scan.Is(at, "+") || _scan.Is(at, "~"))
            {
                if (group is null)
                {
                    Reset();
                }

                at++;
                continue;
            }

            if (_syntax == StyleSyntax.Less && _scan.Is(at, "when") && !Adjacent(at))
            {
                // A Less guard: what follows to the complex selector's end is a condition, no selector.
                at = CommaOrEnd(at, end);
                continue;
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

                Write([Simple.Named(_scan.Text(at) + CssLexer.Unescaped(_scan.Text(at + 1)))]);
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

                    // What a Less :extend() names is the page's rules: a & it writes a suffix to stands for none.
                    Write(Suffixed(group is { Into: not Gathered.Compound } ? [] : parents, CssLexer.Unescaped(_scan.Text(at + 1))));
                    at += 2;
                    continue;
                }

                if (group is null)
                {
                    compound.Parent = true;
                }

                at++;
                continue;
            }

            if (_scan.Is(at, ":"))
            {
                var name = _scan.Is(at + 1, ":") ? at + 2 : at + 1;

                if (!_scan.IsName(name) || name >= end)
                {
                    at = name;
                    continue;
                }

                var paren = name + 1;
                var pseudo = _scan.Text(name);
                var now = group?.Global ?? current;
                var held = Unscoped.Contains(pseudo) || (now && !string.Equals(pseudo, "local", StringComparison.OrdinalIgnoreCase));

                if (paren >= end || !_scan.Is(paren, "(") || !Adjacent(paren))
                {
                    // A :global or a ::v-deep written alone makes the rest of the selector the page's, a :local its own again.
                    if (group is null)
                    {
                        current = held;
                    }
                    else
                    {
                        group.Global = held;
                    }

                    at = paren;
                    continue;
                }

                var (inner, past) = _scan.Bounds(paren, end);
                var extend = _syntax == StyleSyntax.Less && pseudo == "extend";

                if (!Alternatives.Contains(pseudo) && !extend)
                {
                    // A pseudo-class's condition - :not(), :nth-child() - holds no part of the selector.
                    at = past;
                    continue;
                }

                // An :is()'s and the like's selectors are the compound's own; a Less :extend()'s what the rule inherits; an
                // :extend() inside a group, or a group inside an :extend() inside a group, gives the selector nothing.
                groups.Push(group is null
                    ? new Group(extend ? Gathered.Extended : Gathered.Compound, inner, past, held, [], owner: true) { Start = paren + 1 }
                    : new Group(extend ? Gathered.Nothing : group.Into, inner, past, held, group.Held, owner: false) { Start = paren + 1 });
                at = paren + 1;
                continue;
            }

            if (_scan.Is(at, "[") || _scan.Is(at, "("))
            {
                // An attribute's condition, or a Less mixin's parameters: no part of the selector.
                at = _scan.Bounds(at, end).Past;
                continue;
            }

            // An element's name begins a compound: one joined to a namespace's bar, an !important's mark or anything else is none.
            if (_scan.IsName(at) && (at == begins || !Adjacent(at) || _scan.Is(at - 1, ">") || _scan.Is(at - 1, "+") || _scan.Is(at - 1, "~")))
            {
                var name = _scan.Text(at);

                if (Interpolated(at, end))
                {
                    at = Written(at, end, read);
                    continue;
                }

                if (name[0] is not ('$' or '@') && !name.StartsWith("--", StringComparison.Ordinal))
                {
                    Write([Simple.Named(CssLexer.Unescaped(name))]);
                }

                at++;
                continue;
            }

            at++;
        }

        return read;
    }

    /// <summary>
    /// What a <c>&amp;</c> that stands for <paramref name="parents"/> writes with <paramref name="suffix"/> after it: nothing
    /// where it stands for none; the name of one with the suffix written on, where it stands for one of a name of its own;
    /// else the suffix written onto each of them - in a CSS module one for those of each kind its names are exported by
    /// (<see cref="CamelCased"/>), as a module's classes are its own and its elements the page's.
    /// </summary>
    private IReadOnlyList<Simple> Suffixed(IReadOnlyList<Atom> parents, string suffix)
    {
        if (parents.Count == 0)
        {
            return [];
        }

        if (parents is [{ Name: { } name }])
        {
            return [Simple.Named(name + suffix)];
        }

        if (_scope != StyleScope.Module)
        {
            return [Simple.Written(suffix, parents)];
        }

        return [.. parents.GroupBy(atom => atom.Classy ? (atom.Dashed ? 2 : 1) : 0).Select(kind => Simple.Written(suffix, [.. kind]))];
    }

    /// <summary>The index of the comma that ends the complex selector the token at <paramref name="at"/> is in, its brackets passed over; <paramref name="end"/> where none does.</summary>
    private int CommaOrEnd(int at, int end)
    {
        for (; at < end && !_scan.Is(at, ","); at++)
        {
            if (_scan.Opens(at))
            {
                at = _scan.Bounds(at, end).Past - 1;
            }
        }

        return Math.Min(at, end);
    }

    /// <summary>
    /// The selectors a pseudo-class's parentheses held, ended: each part they hold once - the page's only where every place
    /// it is written says so - added to the compound they are written in, or what a Less <c>:extend()</c> names.
    /// </summary>
    private static void Gather(SelectorRead read, Compound compound, ref int subject, Group group)
    {
        var items = new List<Simple>();
        var global = new List<bool>();
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var (item, page) in group.Held)
        {
            if (indices.TryGetValue(item.Key, out var index))
            {
                global[index] &= page;
            }
            else
            {
                indices[item.Key] = items.Count;
                items.Add(item);
                global.Add(page);
            }
        }

        if (group.Into == Gathered.Extended)
        {
            read.Extended.AddRange(items.Where(item => item.Name is not (null or "all")).Select(item => item.Name!));
        }
        else if (items.Count > 0)
        {
            Add(read, compound, ref subject, items, global);
        }
    }

    /// <summary>
    /// Adds what a simple selector writes to the selector's parts - each once, the page's only where every place it is
    /// written says so - and to <paramref name="compound"/>, whose first is the complex selector's subject while no later
    /// compound has a part.
    /// </summary>
    private static void Add(SelectorRead read, Compound compound, ref int subject, IReadOnlyList<Simple> items, IReadOnlyList<bool> global)
    {
        var written = new List<int>();

        for (var at = 0; at < items.Count; at++)
        {
            var item = items[at];

            if (item.Name is { Length: 0 })
            {
                continue;
            }

            if (!read.Indices.TryGetValue(item.Key, out var index))
            {
                read.Indices[item.Key] = index = read.Parts.Count;
                read.Parts.Add(new Part(item, Subject: false, Last: false, global[at]));
            }
            else if (read.Parts[index].Global && !global[at])
            {
                read.Parts[index] = read.Parts[index] with { Global = false };
            }

            if (written.Count == 0 && compound.Empty)
            {
                subject = index;
            }

            compound.Parts.Add(index);
            written.Add(index);
        }

        if (written.Count > 0)
        {
            compound.Wrote(written);
        }
    }

    /// <summary>
    /// A simple selector an interpolation writes, from the token at <paramref name="at"/>: the run of the tokens joined to
    /// it - names, interpolations, numbers and dashes - none of a part's name, said. Answers where the selector goes on.
    /// </summary>
    private int Written(int at, int end, SelectorRead read)
    {
        read.Interpolated = true;

        do
        {
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
    /// A declaration whose colon is at <paramref name="colon"/>: a custom property's a constant over its lines, and a SCSS,
    /// Sass or Less variable's one where the sheet's top declares it - or a <c>!global</c> - but where a block declares it
    /// that block's own; its value read for what it uses and holds, from the constant it is given to or the node that holds
    /// it - an animation's keyframes, a font's families and a module's composed classes among them; the block a Less
    /// detached ruleset or SCSS's nested properties open read in its turn. Answers where the next statement begins.
    /// </summary>
    private int Declaration(Head head, int colon, int to, Place place)
    {
        var at = _scan.Is(head.Start, "*") ? head.Start + 1 : head.Start;
        var name = _scan.IsName(at) && colon == at + 1 ? _scan.Text(at) : null;
        var custom = name is ['-', '-', ..];
        var end = custom ? ValueEnd(colon + 1, to) : head.Stop;
        var from = place.From;

        if (custom || (name is ['$' or '@', _, ..] && (Flagged(colon + 1, end, "global") || place.Locals?.Declare(name) != true)))
        {
            from = Node(name!, CodeSubKinds.StyleVariable, head with { Stop = end, Opens = false }, _scan.Signature(at, end), own: false);
        }

        // What an interpolation in the name uses, and what the value does - a composes' to the file it names, which is an
        // import's path and no literal; the name itself is declared, never used.
        Uses(at, colon, from, place.Locals, names: false);
        Uses(colon + 1, name == "composes" ? Source(colon + 1, end) : end, from, place.Locals);

        var property = name is not null ? Unvendored(name) : null;

        if (property is "animation" or "animation-name")
        {
            Animated(colon + 1, end, from);
        }

        if (property is "font-family" && !place.Face)
        {
            Families(Split(colon + 1, end), from);
        }

        if (property is "font")
        {
            Shorthand(colon + 1, end, from);
        }

        if (name == "composes")
        {
            Composed(colon + 1, end, from, head);
        }

        if (custom)
        {
            return end < to && _scan.Is(end, ";") ? end + 1 : end;
        }

        if (head.Opens)
        {
            var ruleset = name is ['@', ..] && from != place.From;
            Body(head, place, ruleset ? place.Frame.Inside(null, null, from, null) : place.Frame, place.Parents);
        }

        return head.Past;
    }

    /// <summary>Whether the value from <paramref name="from"/> to <paramref name="to"/> carries the flag <c>!</c><paramref name="flag"/>.</summary>
    private bool Flagged(int from, int to, string flag)
    {
        for (var at = from; at + 1 < to; at++)
        {
            if (_scan.Is(at, "!") && _scan.IsName(at + 1) && string.Equals(_scan.Text(at + 1), flag, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// An <c>animation</c>'s or an <c>animation-name</c>'s value, from the token at <paramref name="from"/> to
    /// <paramref name="to"/>: each name or string it writes a use of the keyframes of that name, made from
    /// <paramref name="by"/> - but a word the shorthand gives a meaning of its own (<see cref="AnimationWords"/>), a
    /// function's name and what its parentheses hold, and a variable, which <see cref="Uses"/> reads.
    /// </summary>
    private void Animated(int from, int to, QualifiedName? by)
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
                : _scan[at].Kind == ScanTokenKind.Text && _scan.Text(at) is ['"' or '\'', ..] ? _scan.Value(at) : null;

            if (named is { Length: > 0 })
            {
                _references.Add((by, CodeEdgeKinds.Uses, named, _scan[at].Line, new ExtractQualifier(CodeQualifierKinds.Keyframes)));
            }
        }
    }

    /// <summary>
    /// The families the parts of a font's list name (<paramref name="parts"/>), each a use of the font face of that family,
    /// as CSS matches one (<see cref="Folded"/>), made from <paramref name="by"/>: a string's value, or a run of names
    /// joined by one space - none of a generic family (<see cref="GenericFamilies"/>), a variable or a function.
    /// </summary>
    private void Families(IEnumerable<(int Start, int End)> parts, QualifiedName? by)
    {
        foreach (var (start, end) in parts)
        {
            if (start < end && Family(start, end) is { } family && !GenericFamilies.Contains(family))
            {
                _references.Add((by, CodeEdgeKinds.Uses, Folded(family), _scan[start].Line, new ExtractQualifier(CodeQualifierKinds.FontFace)));
            }
        }
    }

    /// <summary>
    /// A <c>font</c> shorthand's value, from the token at <paramref name="from"/> to <paramref name="to"/>: the families its
    /// list names, as <see cref="Families"/> reads them - the first past the last number its first part writes, the size
    /// (<c>12px</c>, <c>1.2em/1.5</c>) a weight's number comes before - none where it writes no number, as a system font's
    /// keyword does.
    /// </summary>
    private void Shorthand(int from, int to, QualifiedName? by)
    {
        var parts = Split(from, to);
        var (start, end) = parts[0];
        var family = -1;

        for (var at = start; at < end; at++)
        {
            if (_scan[at].Kind == ScanTokenKind.Number)
            {
                family = at + 1;
            }
            else if (_scan.Opens(at))
            {
                at = _scan.Bounds(at, end).Past - 1;
            }
        }

        if (family >= 0)
        {
            Families([(family, end), .. parts.Skip(1)], by);
        }
    }

    /// <summary>
    /// A CSS module's <c>composes: a b</c>: a use of each class of its own sheet it composes, as markup's <c>class</c> is;
    /// and its <c>composes: a b from "./x.css"</c> an import of that file, each class a use of the one that file exports by
    /// that name, through the import alone.
    /// </summary>
    private void Composed(int from, int to, QualifiedName? by, Head head)
    {
        var source = Source(from, to);
        string? prefix = null;

        if (source + 1 < to && _scan[source + 1].Kind == ScanTokenKind.Text && _scan.Value(source + 1) is { Length: > 0 } path)
        {
            // The import is reached by the path it is written with, which no namespace a sheet writes spells.
            prefix = path;

            foreach (var candidate in Candidates(path, "import", plain: true))
            {
                _extract.Import(new ExtractImport(CodeImportForms.Relative, candidate, LineOf(head.Start)) { Prefix = prefix });
            }
        }

        for (var at = from; at < source; at++)
        {
            if (_scan.IsName(at) && _scan.Text(at) is var composed && composed[0] is not ('$' or '@') && !composed.StartsWith("--", StringComparison.Ordinal))
            {
                _references.Add(prefix is not null
                    ? (by, CodeEdgeKinds.Uses, CssLexer.Unescaped(composed), _scan[at].Line, new ExtractQualifier(CodeQualifierKinds.Prefix, prefix))
                    : (by, CodeEdgeKinds.Uses, $".{CssLexer.Unescaped(composed)}", _scan[at].Line, MarkupDialect.Selector));
            }
        }
    }

    /// <summary>The index of a composes' <c>from</c>, from the token at <paramref name="from"/>; <paramref name="to"/> where it names no file.</summary>
    private int Source(int from, int to)
    {
        while (from < to && !_scan.Is(from, "from"))
        {
            from++;
        }

        return from;
    }

    /// <summary>
    /// What the tokens from <paramref name="from"/> to <paramref name="to"/> use and hold, made from <paramref name="by"/>:
    /// each name <see cref="Used"/> reads a use of - but a name <paramref name="locals"/> declares - each string literal's
    /// constant parts a literal of the file, and the code of each interpolation they hold, read in its turn as a text of
    /// its own - one queue of the texts to read, never a call into itself, so interpolations nested to any depth are read,
    /// each scanned once past the ones it holds (<see cref="CssLexer.Closes"/>). Where <paramref name="names"/> is false,
    /// only the literals and the interpolations are read.
    /// </summary>
    private void Uses(int from, int to, QualifiedName? by, Locals? locals, bool names = true)
    {
        var pending = new Queue<(InterpolatedText Text, int From, int To, bool Nested)>();
        pending.Enqueue((_text, from, to, false));

        while (pending.TryDequeue(out var run))
        {
            var scan = run.Text.Scan;

            for (var at = Math.Max(run.From, 0); at < run.To && at < scan.Count; at++)
            {
                var token = scan[at];

                if (token.Kind == ScanTokenKind.Name && (names || run.Nested))
                {
                    Used(scan, at, by, locals);
                    continue;
                }

                if (token.Kind != ScanTokenKind.Text)
                {
                    continue;
                }

                foreach (var (value, line) in scan.LiteralParts(token))
                {
                    _extract.Literal(value, line, by);
                }

                if (run.Text.Interpolations.TryGetValue(token.Start, out var spans))
                {
                    foreach (var (start, end) in spans)
                    {
                        if (start >= 2 && _scan.Source[start - 2] == '@' && _scan.Source[start - 1] == '{')
                        {
                            // Less's @{name}: the variable it names.
                            if (_scan.Source.Slice(start, end).Trim() is { Length: > 0 } variable && variable.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
                                && locals?.Holds($"@{variable}") != true)
                            {
                                _references.Add((by, CodeEdgeKinds.Uses, $"@{variable}", token.Line, new ExtractQualifier(CodeQualifierKinds.StyleVariable)));
                            }

                            continue;
                        }

                        var inner = CssLexer.Scan(_scan.Source, start, end, _text.Lines, _syntax, _closes);
                        pending.Enqueue((inner, 0, inner.Scan.Count, true));
                    }
                }
            }
        }
    }

    /// <summary>
    /// The use the name at <paramref name="at"/> of <paramref name="scan"/> is, made from <paramref name="by"/>: of a custom
    /// property or a variable, a variable's qualifier saying so, a module's variable through its namespace - but a name
    /// <paramref name="locals"/> declares, and a keyword argument's or a configuration's name before its colon; of a function
    /// a call names - SCSS's and Sass's, and CSS's own custom one (<c>--x()</c>), a module's through its namespace, but a
    /// pseudo-class's - the function's qualifier saying so; none of any other name.
    /// </summary>
    private void Used(ScannedText scan, int at, QualifiedName? by, Locals? locals)
    {
        var name = scan.Text(at);
        var joined = at >= 2 && scan.Is(at - 1, ".") && scan[at - 1].End == scan[at].Start && scan.IsName(at - 2) && scan[at - 2].End == scan[at - 1].Start;
        var prefix = joined ? new ExtractQualifier(CodeQualifierKinds.Prefix, scan.Text(at - 2)) : null;

        if (scan.Is(at + 1, "(") && scan[at].End == scan[at + 1].Start && Calls(scan, at, name, joined))
        {
            _references.Add((by, CodeEdgeKinds.Uses, name, scan[at].Line, prefix ?? new ExtractQualifier(CodeQualifierKinds.StyleFunction)));
            return;
        }

        if (name.Length < 2 || !(name.StartsWith("--", StringComparison.Ordinal) ? name.Length > 2 : name[0] is '$' or '@') || scan.Is(at + 1, ":"))
        {
            return;
        }

        if (name[0] == '$' && joined)
        {
            _references.Add((by, CodeEdgeKinds.Uses, name, scan[at].Line, prefix));
            return;
        }

        if (name[0] is not ('$' or '@') || locals?.Holds(name) != true)
        {
            _references.Add((by, CodeEdgeKinds.Uses, name, scan[at].Line, new ExtractQualifier(CodeQualifierKinds.StyleVariable)));
        }
    }

    /// <summary>
    /// Whether the name <paramref name="name"/> at <paramref name="at"/>, its parenthesis after it, calls a function the
    /// sheet may declare: in SCSS and Sass any but a variable's, a special one's (<see cref="SpecialFunctions"/>) and a
    /// pseudo-class's or a selector's - one a module's namespace writes among them - and in CSS its own custom function,
    /// <c>--x()</c>; in Less, which declares none, none.
    /// </summary>
    private bool Calls(ScannedText scan, int at, string name, bool joined)
    {
        if (name.StartsWith("--", StringComparison.Ordinal))
        {
            return _syntax != StyleSyntax.Less && name.Length > 2;
        }

        if (_syntax is not (StyleSyntax.Scss or StyleSyntax.Sass) || name[0] is '$' or '@' || (!joined && SpecialFunctions.Contains(name)))
        {
            return false;
        }

        // A pseudo-class's (:not), a class's or an id's (.m), a placeholder's and a parent's suffix are no function.
        return joined || at == 0 || scan[at - 1].End != scan[at].Start || !(scan.Is(at - 1, ":") || scan.Is(at - 1, ".") || scan.Is(at - 1, "#") || scan.Is(at - 1, "%") || scan.Is(at - 1, "&"));
    }

    // ---- Nodes and the text ----

    /// <summary>
    /// The key of the node of <paramref name="name"/>, a member of <paramref name="subKind"/> over <paramref name="head"/>'s
    /// lines - to its block's close - made where the sheet declares it first and kept where it already stands, one of each
    /// kind and name, and one of each a module's or a component's own besides (<paramref name="own"/>): seen from its file
    /// alone, and where the sheet is a CSS module exported by its name to the scripts that import it, as a module exports
    /// its classes, ids and keyframes. Its id says its kind where its name does not (<see cref="Spelled"/>).
    /// </summary>
    private QualifiedName Node(string name, string subKind, Head head, string signature, bool own)
        => _named.TryGetValue((subKind, name, own), out var known)
            ? known
            : Added((subKind, name, own), name, subKind, Spelled(name, subKind), head, signature, own, own && _scope == StyleScope.Module ? Exported(name, subKind) : null, suffixOf: null);

    /// <summary>
    /// The key of the node of the rule's part <paramref name="item"/>: of its name, as <see cref="Node(string, string, Head, string, bool)"/>
    /// makes one; or, where it is a suffix written onto each of several selectors, of the one node that stands for them all
    /// (<see cref="ExtractNode.SuffixOf"/>) - one of each suffix and selectors - named <c>&amp;</c> and the suffix, its id
    /// the first selector's with the suffix, and where it is a module's own exported by the suffix as each name it is
    /// written onto is exported (<see cref="CamelCased"/>).
    /// </summary>
    private QualifiedName Node(Simple item, Head head, string signature, bool own)
    {
        if (item.Name is { } name)
        {
            return Node(name, CodeSubKinds.Rule, head, signature, own);
        }

        if (_named.TryGetValue((CodeSubKinds.Rule, item.Key, own), out var known))
        {
            return known;
        }

        var (suffix, onto) = (item.Suffix!, item.Onto!);
        var exports = own && _scope == StyleScope.Module && CamelCased(suffix, onto[0].Dashed) is var camel
            ? (camel == suffix ? [$"&{suffix}"] : (IReadOnlyList<string>)[$"&{suffix}", $"&{camel}"])
            : null;

        return Added((CodeSubKinds.Rule, item.Key, own), $"&{suffix}", CodeSubKinds.Rule, $"{_fragments[onto[0].Key]}&{suffix}", head, signature, own, exports, [.. onto.Select(atom => atom.Key)]);    }

    /// <summary>Adds the node of <paramref name="name"/>, known by <paramref name="named"/>, its id's fragment <paramref name="fragment"/>; answers its key.</summary>
    private QualifiedName Added((string SubKind, string Name, bool Own) named, string name, string subKind, string fragment, Head head, string signature, bool own, IReadOnlyList<string>? exports, IReadOnlyList<QualifiedName>? suffixOf)
    {
        var start = LineOf(head.Start);
        var end = head.Opens && head.Close < _scan.Count && _scan.Is(head.Close, "}") && _scan.Match(head.Close) == head.Stop
            ? _scan[head.Close].Line
            : _scan[_scan.LastOf(head.Opens ? head.Close : head.Stop)].EndLine;

        if (own && !_keptDeclared)
        {
            _extract.Scope(Kept, open: false, parent: null);
            _keptDeclared = true;
        }

        var key = _extract.Add(new ExtractNode($"{_language}:{_path}#{fragment}", CodeNodeKinds.Member, name, name, start, Math.Max(start, end), signature.Length > 0 ? signature : ExtractText.Signature(name))
        {
            SubKind = subKind,
            Summary = ScanComments.Summary(_scan, head.Start),
            SeenIn = own ? Kept : null,
            Exports = exports,
            SuffixOf = suffixOf,
        });

        _named[named] = key;
        _fragments[key] = fragment;
        return key;
    }

    /// <summary>
    /// How a node's id spells its name, so no two of one sheet's kinds share one: a rule's and a variable's as they are
    /// written - a class's <c>.</c>, an id's <c>#</c>, a placeholder's <c>%</c>, a variable's <c>$</c>, <c>@</c> or
    /// <c>--</c> tell them apart - and a mixin's, a function's, a keyframes' and a font face's after the word of the
    /// at-rule that declares it: <c>@mixin fade-in</c>, <c>@keyframes fade-in</c>.
    /// </summary>
    private static string Spelled(string name, string subKind) => subKind switch
    {
        CodeSubKinds.StyleMixin => $"@mixin {name}",
        CodeSubKinds.StyleFunction => $"@function {name}",
        CodeSubKinds.Keyframes => $"@keyframes {name}",
        CodeSubKinds.FontFace => $"@font-face {name}",
        _ => name,
    };

    /// <summary>
    /// The key of the sheet's top node, <c>(sheet)</c>, over the lines its text stands on: what a reference no other node
    /// holds is made from - a top-level <c>@include</c>, a flow control's head, a rule of no parts - and every literal
    /// written there, made the first time a reference needs it (<see cref="ScanExtract.Top"/>).
    /// </summary>
    private QualifiedName Top()
    {
        if (_top is not null)
        {
            return _top;
        }

        var first = int.MaxValue;
        var last = 1;

        if (_scan.Count > 0)
        {
            (first, last) = (_scan[0].Line, _scan[_scan.Count - 1].EndLine);
        }

        if (_scan.Comments.Count > 0)
        {
            (first, last) = (Math.Min(first, _scan.Comments[0].Line), Math.Max(last, _scan.Comments[^1].EndLine));
        }

        first = first == int.MaxValue ? 1 : first;
        _top = _extract.Add(new ExtractNode($"{_language}:{_path}#{SheetName}", CodeNodeKinds.Member, SheetName, SheetName, first, Math.Max(first, last), SheetName) { SubKind = CodeSubKinds.Sheet });
        _extract.Top(_top);
        return _top;
    }

    /// <summary>
    /// The names a CSS module exports <paramref name="name"/>, a node of <paramref name="subKind"/>, by: a class's and an
    /// id's without their marker, a keyframes' as it is - each as written and camel-cased (<see cref="CamelCased"/>), as a
    /// bundler's <c>camelCase</c> convention adds it; none of an element or a placeholder.
    /// </summary>
    private static IReadOnlyList<string>? Exported(string name, string subKind)
    {
        var bare = name[0] is '.' or '#' ? name[1..] : subKind == CodeSubKinds.Keyframes ? name : string.Empty;

        if (bare.Length == 0)
        {
            return null;
        }

        var camel = bare[0] + CamelCased(bare[1..], dashed: false);
        return camel != bare ? [bare, camel] : [bare];
    }

    /// <summary>
    /// <paramref name="piece"/>, the part of a name past its first character, camel-cased as a bundler's <c>camelCase</c>
    /// convention does a CSS module's names: each dash or underscore before a letter or a digit taken away and that
    /// character upper-cased, every other dash or underscore taken away - where the name before it ended in one
    /// (<paramref name="dashed"/>), its first character upper-cased for it. The one rule a module's names are camel-cased
    /// by, a suffix written onto them alike.
    /// </summary>
    private static string CamelCased(string piece, bool dashed)
    {
        var cased = new StringBuilder(piece.Length);
        var at = 0;

        if (dashed && piece.Length > 0 && char.IsAsciiLetterOrDigit(piece[0]))
        {
            cased.Append(char.ToUpperInvariant(piece[0]));
            at = 1;
        }

        for (; at < piece.Length; at++)
        {
            if (piece[at] is not ('-' or '_'))
            {
                cased.Append(piece[at]);
            }
            else if (at + 1 < piece.Length && char.IsAsciiLetterOrDigit(piece[at + 1]))
            {
                cased.Append(char.ToUpperInvariant(piece[++at]));
            }
        }

        return cased.ToString();
    }

    /// <summary>The head's signature: its tokens on one line, whole.</summary>
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

    /// <summary>Whether the token at <paramref name="at"/> follows the one before it with nothing between.</summary>
    private bool Adjacent(int at) => at > 0 && at < _scan.Count && _scan[at - 1].End == _scan[at].Start;

    /// <summary>The line the token at <paramref name="at"/> begins on; the text's last where it is past the last.</summary>
    private int LineOf(int at) => _scan.Count == 0 ? 1 : at < _scan.Count ? _scan[at].Line : _scan[_scan.Count - 1].EndLine;
}
