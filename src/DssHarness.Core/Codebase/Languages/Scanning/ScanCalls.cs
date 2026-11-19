using System.Runtime.CompilerServices;

namespace DssHarness.Core.Codebase.Languages.Scanning;

/// <summary>One call a scanned text makes: its name, the parenthesis its arguments open and close, and how many it gives.</summary>
/// <param name="Name">The index of its name's token.</param>
/// <param name="Open">The index of the parenthesis its arguments open.</param>
/// <param name="Close">The index of the one that closes them: past the parenthesis where it is stranded, so it holds nothing; <see cref="ScannedText.Count"/> where none closes it.</param>
/// <param name="Arity">How many arguments it gives: the top-level commas between its parentheses, and one, where anything is between them; <see langword="null"/> where an argument spreads (<see cref="ScanCalls.CallArity"/>).</param>
internal readonly record struct ScannedCall(int Name, int Open, int Close, int? Arity);

/// <summary>What a scanned language says of its calls: the names no call is made by, how it writes a call's type arguments, and whether a comma may end its arguments.</summary>
/// <param name="Keywords">The names of the language no call is made by.</param>
/// <param name="TypeArguments">How it writes a list of type arguments between a call's name and its parenthesis; <see langword="null"/> where it writes none.</param>
/// <param name="TrailingComma">Whether a comma may end a call's arguments with none after it, as Dart's, JavaScript's and PHP's may; where not - C's macros - it is followed by an empty one.</param>
internal sealed record ScanCallRules(IReadOnlySet<string> Keywords, ScanTypeArguments? TypeArguments, bool TrailingComma = false);

/// <summary>
/// How a scanned language writes a list of type arguments, <c>&lt;</c> to <c>&gt;</c>: the punctuators a type it names
/// may hold besides names, numbers, text, nested lists and groups, and what may follow a list in an expression - a
/// call's parenthesis, a scope's separator - so that a list followed by anything else is an expression's comparisons.
/// </summary>
/// <param name="Punctuators">The punctuators a type may hold: C++'s <c>::</c>, <c>*</c> and <c>&amp;</c>, Dart's <c>?</c>.</param>
/// <param name="Followers">What may follow a list a call or a qualified name is written with: C++'s <c>(</c>, <c>::</c> and <c>{</c>.</param>
/// <param name="Conjunctions">
/// Those of <paramref name="Punctuators"/> that, written twice running, join an expression's values but where a list's
/// end or its next piece follows - C++'s <c>&amp;&amp;</c> in code: <c>a &lt; b &amp;&amp; c &gt; (d)</c> is no list,
/// <c>std::forward&lt;T&amp;&amp;&gt;</c> is; none where a punctuator written twice is a type's like any other.
/// </param>
/// <param name="Introducers">The words a literal's list of type arguments may follow, which end no operand: Dart's <c>const</c> in <c>const &lt;K, V&gt;{}</c>; none where it writes no such literal.</param>
/// <param name="Braces">Whether a type may be written in braces, as TypeScript's object type <c>{ id: number }</c> is: a list holds a matched pair of them as it holds a group.</param>
internal sealed record ScanTypeArguments(IReadOnlySet<string> Punctuators, IReadOnlySet<string> Followers, IReadOnlySet<string>? Conjunctions = null, IReadOnlySet<string>? Introducers = null, bool Braces = false);

/// <summary>
/// The structural scanner's reading of a call, the same for every scanned language: a name that is no keyword of the
/// language, an optional list of type arguments, then <c>(</c>, its arity counted by the commas at its own depth - a
/// comma inside a nested bracket, or inside a list of type arguments (<see cref="IsTypeArguments"/>), counts nothing,
/// nor one that ends the arguments where the language lets one.
/// </summary>
internal static class ScanCalls
{
    /// <summary>
    /// The call whose name is the token at <paramref name="at"/>, where one is: the name, then a list of type arguments
    /// where the language writes them, then <c>(</c>; <see langword="null"/> where the name is one of its keywords, or no
    /// parenthesis follows.
    /// </summary>
    /// <param name="scan">The text.</param>
    /// <param name="at">The index of a name.</param>
    /// <param name="rules">What the language says of its calls.</param>
    public static ScannedCall? At(ScannedText scan, int at, ScanCallRules rules)
    {
        if (!scan.IsName(at) || rules.Keywords.Contains(scan.Text(at)))
        {
            return null;
        }

        var open = scan.Next(at);

        if (rules.TypeArguments is { } types && scan.Is(open, "<") && TypeArgumentsEnd(scan, open, types) is var past and >= 0)
        {
            open = past;
        }

        if (!scan.Is(open, "("))
        {
            return null;
        }

        var close = scan.CloseOf(open);
        return new ScannedCall(at, open, close, CallArity(scan, open, close, rules));
    }

    /// <summary>
    /// How many arguments the call whose parentheses are at <paramref name="open"/> and <paramref name="close"/> gives
    /// (<see cref="Arity"/>); <see langword="null"/> where one at their own depth spreads - JavaScript's <c>f(...xs)</c>, PHP's <c>f(...$xs)</c>,
    /// C++'s pack expansion <c>f(args...)</c> - which gives as many as it holds, no count the text says.
    /// </summary>
    /// <param name="scan">The text.</param>
    /// <param name="open">The index of the opening parenthesis.</param>
    /// <param name="close">The index of the closing one, or <see cref="ScannedText.Count"/>.</param>
    /// <param name="rules">What the language says of its calls.</param>
    public static int? CallArity(ScannedText scan, int open, int close, ScanCallRules rules)
    {
        for (var at = open + 1; at < close && at < scan.Count; at = scan.Opens(at) ? Math.Min(scan.Past(at), close) : at + 1)
        {
            // An argument's first or last token: sizeof...(args) spreads nothing.
            if (scan.Is(at, "...") && (scan.Previous(at) is var before && (before == open || scan.Is(before, ",")) || (scan.Next(at) is var after && (after >= close || scan.Is(after, ",")))))
            {
                return null;
            }
        }

        return Arity(scan, open, close, rules);
    }

    /// <summary>
    /// How many arguments the parentheses at <paramref name="open"/> and <paramref name="close"/> give: none where nothing
    /// but directives stands between them, else one more than the commas at their own depth - one that ends them not
    /// counted where the language lets a comma end them.
    /// </summary>
    /// <param name="scan">The text.</param>
    /// <param name="open">The index of the opening parenthesis.</param>
    /// <param name="close">The index of the closing one, or <see cref="ScannedText.Count"/>.</param>
    /// <param name="rules">What the language says of its calls; <see langword="null"/> where only the parentheses count - a macro's parameters.</param>
    public static int Arity(ScannedText scan, int open, int close, ScanCallRules? rules)
    {
        var any = false;
        var commas = 0;
        var endsInComma = false;
        var types = rules?.TypeArguments;

        for (var at = open + 1; at < close; at++)
        {
            if (scan[at].Kind == ScanTokenKind.Directive)
            {
                continue;
            }

            any = true;
            endsInComma = scan.Is(at, ",");

            if (scan.Opens(at))
            {
                at = Math.Min(scan.Past(at), close) - 1;
            }
            else if (types is not null && scan.Is(at, "<") && TypeArgumentsEnd(scan, at, types) is var past and >= 0 && past <= close && IsTypeArguments(scan, at, past, types))
            {
                at = past - 1;
            }
            else if (endsInComma)
            {
                commas++;
            }
        }

        return !any ? 0 : commas + (endsInComma && rules?.TrailingComma == true ? 0 : 1);
    }

    /// <summary>
    /// The index past the list of type arguments the <c>&lt;</c> at <paramref name="less"/> opens in code, where it opens
    /// one and no expression's comparison (<see cref="IsTypeArguments"/>) - a call's <c>make&lt;Widget&gt;(</c>, a
    /// literal's <c>&lt;Item&gt;[]</c>, a scope's <c>Box&lt;T&gt;::</c> - so the names it holds are types a reader references,
    /// no values the code reads; -1 where it opens none.
    /// </summary>
    /// <param name="scan">The text.</param>
    /// <param name="less">The index of a <c>&lt;</c>.</param>
    /// <param name="types">How the language writes type arguments.</param>
    public static int TypeArgumentsAt(ScannedText scan, int less, ScanTypeArguments types)
        => scan.Is(less, "<") && TypeArgumentsEnd(scan, less, types) is var past and >= 0 && IsTypeArguments(scan, less, past, types) ? past : -1;

    /// <summary>
    /// Whether the name at <paramref name="at"/> names a label: written after a <c>goto</c>, or after a <c>break</c> or a
    /// <c>continue</c> on its line - a jump's target, which no value is. A line break after a <c>break</c> ends
    /// JavaScript's statement, so a name on the next line begins one of its own.
    /// </summary>
    /// <param name="scan">The text.</param>
    /// <param name="at">The index of a name.</param>
    /// <param name="caseless">Whether the language's words are alike whatever their case, as PHP's are.</param>
    public static bool NamesLabel(ScannedText scan, int at, bool caseless = false)
    {
        var jump = scan.Previous(at);

        if (!scan.IsName(at) || !scan.IsName(jump))
        {
            return false;
        }

        var comparison = caseless ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var word = scan.Text(jump);

        return word.Equals("goto", comparison)
            || ((word.Equals("break", comparison) || word.Equals("continue", comparison)) && scan[at].Line == scan[jump].EndLine);
    }

    /// <summary>
    /// Whether the list the <c>&lt;</c> at <paramref name="less"/> opens, read as a type's arguments to <paramref name="past"/>,
    /// is one, and no expression's comparisons: a literal's, which no operand ends before and its brackets follow - Dart's
    /// <c>&lt;String, int&gt;{}</c>, <c>&lt;int&gt;[]</c> - or one a name stands before, and after it one of the language's
    /// followers, the text's end, or what no operand begins with and a comparison's <c>&gt;</c> is never followed by -
    /// <c>as Map&lt;String, dynamic&gt;)</c>. A <c>&lt;&lt;</c>'s second <c>&lt;</c> opens neither.
    /// </summary>
    private static bool IsTypeArguments(ScannedText scan, int less, int past, ScanTypeArguments types)
    {
        var before = scan.Previous(less);

        if (!EndsOperand(scan, before, types))
        {
            return scan.Is(past, "[") || scan.Is(past, "{");
        }

        return scan.IsName(before)
            && (past >= scan.Count || types.Followers.Contains(scan.Text(past)) || scan.Is(past, ")") || scan.Is(past, "]") || scan.Is(past, "}")
                || scan.Is(past, ",") || scan.Is(past, ";") || scan.Is(past, ":") || scan.Is(past, "?"));
    }

    /// <summary>
    /// Whether an operand may end at <paramref name="at"/>, so that a <c>&lt;</c> after it may compare: a name but one of
    /// the words a literal's list follows, a number, a text, a group's closer, a non-null assertion's <c>!</c> - one after
    /// an operand, where a negation's comes before one - a postfix <c>++</c> or <c>--</c> (two tokens, as the lexers cut
    /// them). A run of <c>!</c> is stepped back over in a loop, so no run of them reaches past the reader's stack.
    /// </summary>
    private static bool EndsOperand(ScannedText scan, int at, ScanTypeArguments types)
    {
        while (scan.Is(at, "!"))
        {
            at = scan.Previous(at);
        }

        return at >= 0 && at < scan.Count
            && ((scan[at].Kind == ScanTokenKind.Name && types.Introducers?.Contains(scan.Text(at)) != true)
                || scan[at].Kind is ScanTokenKind.Number or ScanTokenKind.Text
                || scan.Is(at, ")") || scan.Is(at, "]")
                || ((scan.Is(at, "+") || scan.Is(at, "-")) && scan.Is(scan.Previous(at), scan.Text(at))));
    }

    /// <summary>
    /// The spans between the top-level commas of a list of declarations from <paramref name="from"/> to
    /// <paramref name="to"/> - parameters, a template's parameters, declarators - groups passed over, and a type's
    /// arguments wherever a list of them stands after a name; none where only directives stand between.
    /// </summary>
    /// <param name="scan">The text.</param>
    /// <param name="from">The index of the list's first token.</param>
    /// <param name="to">The index past its last.</param>
    /// <param name="types">How the language writes type arguments; <see langword="null"/> where it writes none.</param>
    public static List<(int Start, int End)> Split(ScannedText scan, int from, int to, ScanTypeArguments? types)
    {
        var pieces = new List<(int, int)>();
        var start = -1;

        for (var at = from; at < to && at < scan.Count; at++)
        {
            if (scan[at].Kind == ScanTokenKind.Directive)
            {
                continue;
            }

            if (scan.Is(at, ","))
            {
                if (start >= 0)
                {
                    pieces.Add((start, at));
                }

                start = -1;
                continue;
            }

            start = start < 0 ? at : start;
            at = Passed(scan, at, to, types);
        }

        if (start >= 0)
        {
            pieces.Add((start, Math.Min(to, scan.Count)));
        }

        return pieces;
    }

    /// <summary>
    /// Whether the tokens from <paramref name="start"/> to <paramref name="end"/> hold a literal - a number or a text - at
    /// their top, groups and a type's arguments passed over: what an argument holds, and no declaration's parameter does.
    /// </summary>
    /// <param name="scan">The text.</param>
    /// <param name="start">The index of the first token.</param>
    /// <param name="end">The index past the last.</param>
    /// <param name="types">How the language writes type arguments; <see langword="null"/> where it writes none.</param>
    public static bool HoldsLiteral(ScannedText scan, int start, int end, ScanTypeArguments? types)
    {
        for (var at = start; at < end && at < scan.Count; at++)
        {
            if (Passed(scan, at, end, types) is var passed && passed > at)
            {
                at = passed;
            }
            else if (scan[at].Kind is ScanTokenKind.Number or ScanTokenKind.Text)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The index of the first token written <paramref name="text"/> at the top of the tokens from <paramref name="start"/>
    /// to <paramref name="end"/>, groups and a type's arguments passed over and none <paramref name="other"/> says is
    /// another's - the <c>=</c> of C++'s <c>operator+=</c>: a parameter's default, past <c>enable_if_t&lt;N == 2&gt;</c>;
    /// <paramref name="end"/> where none is.
    /// </summary>
    /// <param name="scan">The text.</param>
    /// <param name="start">The index of the first token.</param>
    /// <param name="end">The index past the last.</param>
    /// <param name="text">What the token is written.</param>
    /// <param name="types">How the language writes type arguments; <see langword="null"/> where it writes none.</param>
    /// <param name="other">Whether a token so written is another's; none is where it is not given.</param>
    public static int TopLevel(ScannedText scan, int start, int end, string text, ScanTypeArguments? types, Func<int, bool>? other = null)
    {
        for (var at = start; at < end && at < scan.Count; at++)
        {
            if (scan.Is(at, text) && other?.Invoke(at) != true)
            {
                return at;
            }

            at = Passed(scan, at, end, types);
        }

        return end;
    }

    /// <summary>
    /// Whether the colon at <paramref name="colon"/> is a conditional's - <c>c ? a : b</c> - as more question marks than
    /// colons before it say, back to the start of its operand: its statement's, its group's or a comma's at the top. A
    /// label's, a case's, a key's, a named argument's and a type's are not; a language whose <c>?.</c>, <c>??</c> or
    /// <c>?-&gt;</c> is a token of its own holds no lone question mark but a conditional's or a nullable type's.
    /// </summary>
    /// <param name="scan">The text.</param>
    /// <param name="colon">The index of the colon.</param>
    public static bool IsConditionalColon(ScannedText scan, int colon)
    {
        var open = 0;

        for (var at = scan.Previous(colon); at >= 0 && !scan.Is(at, ";") && !scan.Is(at, "}") && !scan.Is(at, ",") && !scan.Opens(at); at = scan.Previous(at))
        {
            if ((scan.Is(at, ")") || scan.Is(at, "]")) && scan.Match(at) is var match and >= 0)
            {
                at = match;
                continue;
            }

            open += scan.Is(at, "?") ? 1 : scan.Is(at, ":") ? -1 : 0;

            if (open > 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Where each later declarator of the declaration whose first declarator begins at <paramref name="first"/> begins -
    /// the token after each comma at the declaration's own level, <c>b</c> and <c>*c</c> of <c>int a = 1, b, *c;</c> -
    /// up to the statement's end, the closer of what holds it or <paramref name="end"/>; where <paramref name="lineEnds"/>,
    /// as a line break may end JavaScript's statement, up to a line the declaration does not go on to with a comma. What
    /// begins there is the reader's to read: a declarator's name, or a pattern or a type's commas where it is none.
    /// </summary>
    /// <param name="scan">The text.</param>
    /// <param name="first">The index of the first declarator's first token: its name, or the pattern it opens.</param>
    /// <param name="end">The index the declaration ends before at the latest.</param>
    /// <param name="lineEnds">Whether a line break the declaration does not go on past with a comma ends it.</param>
    public static List<int> LaterDeclarators(ScannedText scan, int first, int end, bool lineEnds)
    {
        var starts = new List<int>();
        var last = first;

        for (var at = first; at >= 0 && at < end && !scan.EndsStatement(at) && !scan.Is(at, ")") && !scan.Is(at, "]") && !scan.Is(at, "}"); at = scan.Next(at))
        {
            if (lineEnds && at != first && scan[at].Line > scan[last].EndLine && !scan.Is(last, ",") && !scan.Is(at, ","))
            {
                break;
            }

            if (scan.Opens(at))
            {
                if (scan.Match(at) is not (var close and > 0))
                {
                    break;
                }

                at = close;
            }
            else if (scan.Is(at, ",") && scan.Next(at) < end)
            {
                starts.Add(scan.Next(at));
            }

            last = at;
        }

        return starts;
    }

    /// <summary>
    /// The index of the last token of what begins at <paramref name="at"/> as one piece within <paramref name="end"/> -
    /// a group, or a type's arguments after a name - and <paramref name="at"/> itself where neither begins there.
    /// </summary>
    private static int Passed(ScannedText scan, int at, int end, ScanTypeArguments? types)
        => scan.Opens(at) ? Math.Max(at, Math.Min(scan.Past(at), end) - 1)
            : types is not null && scan.Is(at, "<") && scan.IsName(scan.Previous(at)) && TypeArgumentsEnd(scan, at, types) is var past and > 0 && past <= end ? past - 1
            : at;

    /// <summary>
    /// The index past the <c>&gt;</c> that ends the type-argument list the <c>&lt;</c> at <paramref name="less"/> opens:
    /// a list of names, numbers, text, the language's punctuators and nested lists and groups, however long; -1 where what
    /// follows is none - an expression's comparison. Each list's end is found once for a text and a way of writing them
    /// (<see cref="AngleTable"/>): the read of one list says where each list nested in it ends, and a later read of a
    /// list passes over one nested in it whose end is known - or stops where that one was found to be none, for what
    /// stopped it stops the outer list too - so the lists of a text are read in time linear in its tokens.
    /// </summary>
    /// <param name="scan">The text.</param>
    /// <param name="less">The index of the <c>&lt;</c>.</param>
    /// <param name="types">How the language writes type arguments.</param>
    public static int TypeArgumentsEnd(ScannedText scan, int less, ScanTypeArguments types)
    {
        // What no < opens is no list.
        if (less < 0 || less >= scan.Count || !scan.Is(less, "<"))
        {
            return -1;
        }

        var ends = AngleTable.Of(scan).EndsOf(types);

        if (ends.TryGetValue(less, out var known))
        {
            return known;
        }

        // The lists this read is inside, innermost last: the first is the one asked for.
        var open = new List<int>();

        for (var at = less; at < scan.Count; at++)
        {
            var token = scan[at];

            switch (token.Kind)
            {
                case ScanTokenKind.Directive:
                case ScanTokenKind.Name:
                case ScanTokenKind.Number:
                case ScanTokenKind.Text:
                    continue;
            }

            if (scan.Is(at, "<"))
            {
                if (at != less && ends.TryGetValue(at, out var nested))
                {
                    if (nested < 0)
                    {
                        break;
                    }

                    at = nested - 1;
                    continue;
                }

                open.Add(at);
            }
            else if (scan.Is(at, ">"))
            {
                ends[open[^1]] = at + 1;
                open.RemoveAt(open.Count - 1);

                if (open.Count == 0)
                {
                    return at + 1;
                }
            }
            else if (scan.Is(at, "(") || scan.Is(at, "[") || (types.Braces && scan.Is(at, "{")))
            {
                if (scan.Match(at) < 0)
                {
                    break;
                }

                at = scan.Match(at);
            }
            else if (!types.Punctuators.Contains(scan.Text(at)) || Conjoins(scan, at, types))
            {
                break;
            }
        }

        // Every list still open where the read stopped stops there too: none of them is one.
        foreach (var unended in open)
        {
            ends[unended] = -1;
        }

        return -1;
    }

    /// <summary>
    /// The index of the <c>&lt;</c> the <c>&gt;</c> at <paramref name="greater"/> closes, its list a type's arguments by
    /// <paramref name="types"/>; -1 where none does. The <c>&lt;</c> is the one the <c>&gt;</c> matches read back over its
    /// statement - groups passed over whole, back to a statement's end or a brace - as one pass over the text finds every
    /// <c>&gt;</c>'s (<see cref="AngleTable"/>), however long the list.
    /// </summary>
    /// <param name="scan">The text.</param>
    /// <param name="greater">The index of the <c>&gt;</c>.</param>
    /// <param name="types">How the language writes type arguments where the list stands.</param>
    public static int TypeArgumentsStart(ScannedText scan, int greater, ScanTypeArguments types)
        => greater >= 0 && greater < scan.Count && AngleTable.Of(scan).Opener(greater) is var less and >= 0 && TypeArgumentsEnd(scan, less, types) == greater + 1 ? less : -1;

    /// <summary>
    /// What a text's angle brackets are, found once for the text: the <c>&lt;</c> each <c>&gt;</c> matches, read back as a
    /// list's closer is - its groups passed over whole, a statement's end or a brace ending the read - and, for each way a
    /// language writes type arguments, where each list a <c>&lt;</c> opens ends (<see cref="TypeArgumentsEnd"/>).
    /// </summary>
    private sealed class AngleTable
    {
        /// <summary>Each text's table, kept while the text is.</summary>
        private static readonly ConditionalWeakTable<ScannedText, AngleTable> Tables = new();

        private readonly ScannedText _scan;
        private readonly Dictionary<ScanTypeArguments, Dictionary<int, int>> _ends = new(ReferenceEqualityComparer.Instance);
        private int[]? _openers;

        private AngleTable(ScannedText scan) => _scan = scan;

        /// <summary>The table of <paramref name="scan"/>, made the first time it is asked for.</summary>
        public static AngleTable Of(ScannedText scan) => Tables.GetValue(scan, text => new AngleTable(text));

        /// <summary>Where each list a <c>&lt;</c> opens ends, read as <paramref name="types"/> writes them: filled as lists are read.</summary>
        public Dictionary<int, int> EndsOf(ScanTypeArguments types)
            => _ends.TryGetValue(types, out var ends) ? ends : _ends[types] = [];

        /// <summary>The index of the <c>&lt;</c> the <c>&gt;</c> at <paramref name="greater"/> matches; -1 where none does.</summary>
        public int Opener(int greater) => (_openers ??= Openers())[greater];

        /// <summary>
        /// The <c>&lt;</c> each <c>&gt;</c> matches, in one pass forward: what read back from a <c>&gt;</c> it would meet -
        /// a <c>&lt;</c> no <c>&gt;</c> between closed, a group's insides passed over and what stood before the group seen
        /// again past it, nothing past a statement's end or a brace - as a stack of the open <c>&lt;</c>s, kept as a list
        /// of links so that a group's closer takes back the stack its opener saw, in one step.
        /// </summary>
        private int[] Openers()
        {
            var openers = new int[_scan.Count];
            var before = new Dictionary<int, Opened?>();
            Opened? open = null;

            for (var at = 0; at < _scan.Count; at++)
            {
                openers[at] = -1;

                if (_scan.Is(at, "<"))
                {
                    open = new Opened(at, open);
                }
                else if (_scan.Is(at, ">"))
                {
                    if (open is not null)
                    {
                        openers[at] = open.At;
                        open = open.Below;
                    }
                }
                else if (_scan.Is(at, "(") || _scan.Is(at, "["))
                {
                    before[at] = open;
                }
                else if (_scan.Is(at, ")") || _scan.Is(at, "]"))
                {
                    // Past a group, a > reads back to its opener and on before it: the stack the opener saw. A closer no
                    // opener matches stops any read back across it.
                    open = _scan.Match(at) is var opener and >= 0 && before.TryGetValue(opener, out var seen) ? seen : null;
                }
                else if (_scan.Is(at, ";") || _scan.Is(at, "{") || _scan.Is(at, "}"))
                {
                    // A brace a closer matches is passed over from that closer, as a group's opener is.
                    if (_scan.Is(at, "{"))
                    {
                        before[at] = open;
                    }

                    open = null;
                }
            }

            return openers;
        }

        /// <summary>One open <c>&lt;</c>, and those open below it.</summary>
        private sealed record Opened(int At, Opened? Below);
    }

    /// <summary>
    /// Whether the punctuator at <paramref name="at"/> is the second of a conjunction the language writes twice running,
    /// as C++'s <c>&amp;&amp;</c> is in code - what follows it no list's end and no next piece's beginning.
    /// </summary>
    private static bool Conjoins(ScannedText scan, int at, ScanTypeArguments types)
    {
        var before = scan.Previous(at);
        var next = scan.Next(at);

        return types.Conjunctions?.Contains(scan.Text(at)) == true && scan.Is(before, scan.Text(at))
            && !(scan.Is(next, ">") || scan.Is(next, ",") || scan.Is(next, "..."));
    }
}
