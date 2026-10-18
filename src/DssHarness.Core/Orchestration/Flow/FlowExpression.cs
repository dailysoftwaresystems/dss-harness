namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// One expression - a <c>when</c>, an <c>until</c>, a <c>blocking</c>, a <c>forEach</c>, a <c>leftover</c> - as a
/// tree: what it reads, how it joins and compares, and where it looks into a list with <c>any</c>, parsed once
/// here and asked of by whatever evaluates it.
/// </summary>
/// <remarks>
/// <para>
/// The grammar, tightest-binding last: <c>or = and { "||" and }</c>, <c>and = cmp { "&amp;&amp;" cmp }</c>,
/// <c>cmp = not [ ("==" | "!=") not ]</c>, <c>not = "!" not | coalesce</c>, <c>coalesce = primary { "??" primary
/// }</c>, and a primary is a group, <c>empty(...)</c>, <c>any(list, test)</c>, a literal, a <c>{path}</c>
/// placeholder or a path. A path is a root - a step's name, <c>previous</c> with a step's name, one of the run's
/// own names - walked by keys and integer indexes. Identifiers are letters, underscores, digits and dashes, so a
/// step's name needs no quoting; there is no arithmetic, for expressions select and never compute.
/// </para>
/// <para>
/// <see cref="WorkflowExpressions"/> stays the one owner of the names an expression reads and of what must be
/// written whole: that class scans, this one parses, and where an expression parses, its reads here are the reads
/// that class finds - pinned by test, so the two cannot drift. A path with an index reads as one name here where
/// the scanner reads its segments apart, each checked on its own; both spell the same names.
/// </para>
/// </remarks>
public abstract record FlowExpression
{
    /// <summary>
    /// Parses <paramref name="text"/> as one expression, refusing what the grammar does not say: the refusal
    /// names the column it stopped at, as <see cref="WorkflowExpressions.SyntaxProblem"/> does.
    /// </summary>
    /// <param name="text">The expression, as a definition file wrote it.</param>
    /// <exception cref="ArgumentException">The text is empty, or is not one expression as the grammar spells one.</exception>
    public static FlowExpression Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var parser = new Parser(text);
        var expression = parser.Or();

        if (!parser.Done)
        {
            var stuck = parser.Stuck;
            throw new ArgumentException($"column {stuck + 1}: '{text[stuck]}' is not part of an expression", nameof(text));
        }

        return expression;
    }

    /// <summary>
    /// Why <paramref name="text"/> is not one expression as the grammar spells it, or <see langword="null"/> where it
    /// is: the reason alone, for a refusal to quote.
    /// </summary>
    /// <param name="text">The expression, as a definition file wrote it.</param>
    public static string? GrammarProblem(string text)
    {
        try
        {
            _ = Parse(text);
            return null;
        }
        catch (ArgumentException problem)
        {
            return problem.Message.Replace($" (Parameter '{problem.ParamName}')", string.Empty, StringComparison.Ordinal);
        }
    }

    /// <summary>Every name this expression reads, in the order it reads them, once each: the tree's own account, agreeing with <see cref="WorkflowExpressions.ReadsOf"/>.</summary>
    public IReadOnlyList<WorkflowExpressions.Read> Reads()
    {
        var reads = new List<WorkflowExpressions.Read>();
        Collect(reads, insideAny: false);
        return reads;
    }

    /// <summary>Gathers this node's reads into <paramref name="reads"/>, once each, in order.</summary>
    protected internal virtual void Collect(List<WorkflowExpressions.Read> reads, bool insideAny)
    {
    }

    /// <summary>Adds <paramref name="read"/> where its whole name is not read already, as the scanner adds one.</summary>
    private protected static void Add(List<WorkflowExpressions.Read> reads, WorkflowExpressions.Read read)
    {
        if (reads.All(earlier => !string.Equals(earlier.Whole, read.Whole, StringComparison.Ordinal)))
        {
            reads.Add(read);
        }
    }

    /// <summary><c>a || b || c</c>: true where any term is true, short-circuiting left to right.</summary>
    public sealed record Or(IReadOnlyList<FlowExpression> Terms) : FlowExpression
    {
        protected internal override void Collect(List<WorkflowExpressions.Read> reads, bool insideAny)
        {
            foreach (var term in Terms)
            {
                term.Collect(reads, insideAny);
            }
        }
    }

    /// <summary><c>a &amp;&amp; b</c>: true where every term is true, short-circuiting left to right.</summary>
    public sealed record And(IReadOnlyList<FlowExpression> Terms) : FlowExpression
    {
        protected internal override void Collect(List<WorkflowExpressions.Read> reads, bool insideAny)
        {
            foreach (var term in Terms)
            {
                term.Collect(reads, insideAny);
            }
        }
    }

    /// <summary><c>a == b</c> or <c>a != b</c>: two scalars, or null, compared for equality or its opposite.</summary>
    public sealed record Compare(FlowExpression Left, string Operator, FlowExpression Right) : FlowExpression
    {
        protected internal override void Collect(List<WorkflowExpressions.Read> reads, bool insideAny)
        {
            Left.Collect(reads, insideAny);
            Right.Collect(reads, insideAny);
        }
    }

    /// <summary><c>!a</c>: the opposite of a boolean.</summary>
    public sealed record Not(FlowExpression Inner) : FlowExpression
    {
        protected internal override void Collect(List<WorkflowExpressions.Read> reads, bool insideAny)
            => Inner.Collect(reads, insideAny);
    }

    /// <summary><c>a ?? b</c>: the left side where it is there and not null, the right otherwise.</summary>
    public sealed record Coalesce(IReadOnlyList<FlowExpression> Terms) : FlowExpression
    {
        protected internal override void Collect(List<WorkflowExpressions.Read> reads, bool insideAny)
        {
            foreach (var term in Terms)
            {
                term.Collect(reads, insideAny);
            }
        }
    }

    /// <summary><c>empty(x)</c>: true for null, <c>""</c>, <c>[]</c>, <c>{}</c> and a name that reads nothing.</summary>
    public sealed record Empty(FlowExpression Inner) : FlowExpression
    {
        protected internal override void Collect(List<WorkflowExpressions.Read> reads, bool insideAny)
        {
            // The scanner reads the word itself, as a root of the run's own: the tree reads it too, so the two
            // agree on every expression.
            Add(reads, new WorkflowExpressions.Read("empty", "empty", Template: false, insideAny));
            Inner.Collect(reads, insideAny);
        }
    }

    /// <summary>
    /// <c>any(list, test)</c>: true where <c>test</c> is true of at least one member of <c>list</c>, the member
    /// named <c>it</c> inside the test.
    /// </summary>
    public sealed record Any(FlowExpression List, FlowExpression Test) : FlowExpression
    {
        protected internal override void Collect(List<WorkflowExpressions.Read> reads, bool insideAny)
        {
            // The scanner marks every read within an any(...) call, its list as its test: the tree reads it the
            // same, so the two agree on every expression, wherever the member's own name binds.
            List.Collect(reads, insideAny: true);
            Test.Collect(reads, insideAny: true);
        }
    }

    /// <summary>A literal as written: <c>true</c>, <c>false</c>, <c>null</c>, a number, or a quoted string.</summary>
    public sealed record Literal(object? Value) : FlowExpression;

    /// <summary>A <c>{path}</c> placeholder read inside an expression: a read of its own, filled as templates are.</summary>
    public sealed record Placeholder(string Whole) : FlowExpression
    {
        protected internal override void Collect(List<WorkflowExpressions.Read> reads, bool insideAny)
            => Add(reads, Read(Whole, Template: true, insideAny));
    }

    /// <summary>One step of a path: a key of what the last step named, or an index into its list.</summary>
    public sealed record Segment(string? Key, int? Index)
    {
        /// <summary>Whether this segment is a key.</summary>
        public bool IsKey => Key is not null;
    }

    /// <summary>
    /// A dotted name: where it starts - a step's name, <c>previous</c> with a step's name, or one of the run's own
    /// roots - and the keys and integer indexes that walk what it names.
    /// </summary>
    public sealed record Path(string Root, IReadOnlyList<Segment> Steps) : FlowExpression
    {
        /// <summary>The whole name as the expression wrote it, indexes included.</summary>
        public string Whole => $"{Root}{string.Concat(Steps.Select(step => step.IsKey ? $".{step.Key}" : $"[{step.Index}]"))}";

        protected internal override void Collect(List<WorkflowExpressions.Read> reads, bool insideAny)
            => Add(reads, Read(Whole, Template: false, insideAny));
    }

    /// <summary>One read as the scanner spells it: root, whole name, template or not, and whether it sits in an <c>any</c> test.</summary>
    private static WorkflowExpressions.Read Read(string whole, bool Template, bool insideAny)
    {
        var segments = whole.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var root = segments.Length > 0 ? segments[0] : whole;

        return new WorkflowExpressions.Read(root, string.Join('.', segments), Template, insideAny);
    }

    /// <summary>The recursive-descent reader of the grammar, over the text as written.</summary>
    private sealed class Parser(string text)
    {
        /// <summary>The most a group may nest: a guard, so no file can spend the stack.</summary>
        private const int MostNesting = 64;

        private readonly string _text = text;

        private int _depth;

        /// <summary>The parameter a refusal names.</summary>
        private const string Text = nameof(text);

        /// <summary>Where the reader sits.</summary>
        public int At { get; private set; }

        /// <summary>Whether the whole text has been read.</summary>
        public bool Done => Skip() >= _text.Length;

        /// <summary>Where the reader sits, past any space: the column a refusal names.</summary>
        public int Stuck => Skip();

        /// <summary>or = and { "||" and }</summary>
        public FlowExpression Or()
        {
            var terms = new List<FlowExpression> { And() };

            while (Takes("||"))
            {
                terms.Add(And());
            }

            return terms.Count == 1 ? terms[0] : new Or(terms);
        }

        /// <summary>and = cmp { "&amp;&amp;" cmp }</summary>
        private FlowExpression And()
        {
            var terms = new List<FlowExpression> { Cmp() };

            while (Takes("&&"))
            {
                terms.Add(Cmp());
            }

            return terms.Count == 1 ? terms[0] : new And(terms);
        }

        /// <summary>cmp = not [ ("==" | "!=") not ]</summary>
        private FlowExpression Cmp()
        {
            var left = Not();

            if (Takes("=="))
            {
                return new Compare(left, "==", Not());
            }

            return Takes("!=") ? new Compare(left, "!=", Not()) : left;
        }

        /// <summary>not = "!" not | coalesce</summary>
        private FlowExpression Not()
            => Takes("!") ? new Not(Not()) : Coalesce();

        /// <summary>coalesce = primary { "??" primary }</summary>
        private FlowExpression Coalesce()
        {
            var terms = new List<FlowExpression> { Primary() };

            while (Takes("??"))
            {
                terms.Add(Primary());
            }

            return terms.Count == 1 ? terms[0] : new Coalesce(terms);
        }

        /// <summary>primary = "(" or ")" | empty( or ) | any( or , or ) | literal | placeholder | path</summary>
        private FlowExpression Primary()
        {
            var at = Skip();

            if (at >= _text.Length)
            {
                throw new ArgumentException($"the expression ends at column {at + 1} where a value was to be read", Text);
            }

            if (_text[at] == '(')
            {
                if (++_depth > MostNesting)
                {
                    throw new ArgumentException($"the expression nests more than {MostNesting} groups deep", Text);
                }

                At = at + 1;
                var inner = Or();
                Expect(')');
                _depth--;
                return inner;
            }

            if (_text[at] == '{')
            {
                var end = _text.IndexOf('}', at + 1);

                if (end < 0)
                {
                    throw new ArgumentException($"the placeholder at column {at + 1} is never closed", Text);
                }

                At = end + 1;
                return new Placeholder(_text[(at + 1)..end].Trim());
            }

            if (_text[at] is '\'' or '"')
            {
                return String(at);
            }

            if (char.IsDigit(_text[at]))
            {
                return Number(at);
            }

            if (StartsName(_text[at]))
            {
                var (name, next) = Name(at);
                At = next;

                if (string.Equals(name, "empty", StringComparison.Ordinal))
                {
                    Expect('(');
                    var inner = Or();
                    Expect(')');
                    return new Empty(inner);
                }

                if (string.Equals(name, "any", StringComparison.Ordinal))
                {
                    Expect('(');
                    var list = Or();
                    Expect(',');
                    var test = Or();
                    Expect(')');
                    return new Any(list, test);
                }

                if (name is "true" or "false")
                {
                    return new Literal(name == "true");
                }

                if (name == "null")
                {
                    return new Literal(null);
                }

                var walked = Walk(name);

                if (string.Equals(name, "previous", StringComparison.Ordinal) && walked is FlowExpression.Path { Steps: [] })
                {
                    throw new ArgumentException($"column {at + 1}: 'previous' names a step's work; say which one", Text);
                }

                return walked;
            }

            throw new ArgumentException($"column {at + 1}: '{_text[at]}' does not start a value", Text);
        }

        /// <summary>path = root { "." key | "[" int "]" }, the root already read.</summary>
        private FlowExpression Walk(string root)
        {
            var steps = new List<Path.Segment>();

            while (true)
            {
                var at = Skip();

                if (at < _text.Length && _text[at] == '.' && at + 1 < _text.Length && StartsName(_text[at + 1]))
                {
                    var (key, next) = Name(at + 1);
                    steps.Add(new Path.Segment(key, null));
                    At = next;
                }
                else if (at < _text.Length && _text[at] == '[')
                {
                    var close = _text.IndexOf(']', at + 1);

                    if (close < 0
                        || !int.TryParse(_text[(at + 1)..close], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index)
                        || index < 0)
                    {
                        throw new ArgumentException($"the index at column {at + 1} is not a whole number from 0", Text);
                    }

                    steps.Add(new Path.Segment(null, index));
                    At = close + 1;
                }
                else
                {
                    return new Path(root, steps);
                }
            }
        }

        /// <summary>A quoted string literal, its quote at <paramref name="at"/>.</summary>
        private FlowExpression String(int at)
        {
            var quote = _text[at];
            var end = _text.IndexOf(quote, at + 1);

            if (end < 0)
            {
                throw new ArgumentException($"the quote at column {at + 1} is never closed", Text);
            }

            At = end + 1;
            return new Literal(_text[(at + 1)..end]);
        }

        /// <summary>A number literal, its digits at <paramref name="at"/>.</summary>
        private FlowExpression Number(int at)
        {
            var end = at;

            while (end < _text.Length && (char.IsDigit(_text[end]) || _text[end] == '.'))
            {
                end++;
            }

            At = end;

            var text = _text[at..end];

            if (long.TryParse(text, out var whole))
            {
                return new Literal(whole);
            }

            return double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fraction)
                ? new Literal(fraction)
                : throw new ArgumentException($"column {at + 1}: '{text}' is not a number an expression reads", Text);
        }

        /// <summary>Reads <paramref name="operator"/> where the text has it next, moving past it.</summary>
        private bool Takes(string @operator)
        {
            var at = Skip();

            if (at + @operator.Length > _text.Length || !string.Equals(_text.Substring(at, @operator.Length), @operator, StringComparison.Ordinal))
            {
                return false;
            }

            At = at + @operator.Length;
            return true;
        }

        /// <summary>Reads <paramref name="character"/> where the text has it next, or refuses naming where it sat.</summary>
        private void Expect(char character)
        {
            var at = Skip();

            if (at >= _text.Length)
            {
                throw new ArgumentException($"the expression ends before its '{character}' at column {at + 1}", Text);
            }

            if (_text[at] != character)
            {
                throw new ArgumentException($"column {at + 1}: '{character}' was to be read, and '{_text[at]}' is", Text);
            }

            At = at + 1;
        }

        /// <summary>The next position the text has something to say.</summary>
        private int Skip()
        {
            var at = At;

            while (at < _text.Length && char.IsWhiteSpace(_text[at]))
            {
                at++;
            }

            return at;
        }

        /// <summary>Reads one identifier: letters, underscores, digits and dashes.</summary>
        private (string Name, int Next) Name(int at)
        {
            var end = at;

            while (end < _text.Length && (char.IsLetterOrDigit(_text[end]) || _text[end] is '_' or '-'))
            {
                end++;
            }

            return (_text[at..end], end);
        }

        /// <summary>A name starts with a letter or an underscore.</summary>
        private static bool StartsName(char character) => char.IsLetter(character) || character == '_';
    }
}
