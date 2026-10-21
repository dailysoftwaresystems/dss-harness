using System.Globalization;
using DssHarness.Core.Schemas;

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
/// own names - walked by keys and integer indexes, and written whole: no space stands before its dots or its
/// brackets, so <c>review .findings</c> is two values side by side and refused, never one path. Identifiers are
/// letters, underscores, digits and dashes, so a step's name needs no quoting; there is no arithmetic, for
/// expressions select and never compute.
/// </para>
/// <para>
/// Every expression ends: nothing nests more than <see cref="MostNesting"/> deep - a group, a <c>!</c>, an
/// <c>empty(...)</c> and an <c>any(...)</c> inside another each go one deeper, as does each name of a path after
/// its first - so no text a file holds can spend the stack of the process that reads it.
/// </para>
/// <para>
/// <see cref="WorkflowExpressions"/> stays the one owner of the names an expression reads and of what must be
/// written whole: that class scans, this one parses, and where an expression parses, its reads here are the reads
/// that class finds - pinned by test, so the two cannot drift. Both spell a read through the one
/// <see cref="WorkflowExpressions.ReadOf"/>, an indexed path as one read whose root is its first name.
/// </para>
/// </remarks>
public abstract record FlowExpression
{
    /// <summary>The most an expression nests: a guard, so no file can spend the stack however it is written.</summary>
    public const int MostNesting = 64;

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

    /// <summary>
    /// What this expression yields, as far as the definition says, with every operator held to what it takes when the
    /// definition is loaded: <c>!</c>, <c>&amp;&amp;</c> and <c>||</c> a boolean, <c>==</c> and <c>!=</c> no list and
    /// no object, <c>any</c> a list and a test that yields a boolean; <c>??</c> and <c>empty(...)</c> take anything. A
    /// value is refused only where no value its reading allows is what the operator takes: one that may be null, or
    /// whose kind nothing says, is the run's to read, which refuses null and Missing then.
    /// </summary>
    /// <param name="read">
    /// What a path yields - a step's output or one of its facts, an input, one of the run's own names - for the caller
    /// to say, and to refuse where nothing holds it, answering <see cref="JsonSchemaSubset.Reading.Anything"/> then. A
    /// path from <c>it</c> is read here, as an item of the list the nearest <c>any(...)</c> looks into.
    /// </param>
    /// <param name="problem">Takes each misuse, said as what follows the expression's own name: <c>applies ! to ...</c>.</param>
    public JsonSchemaSubset.Reading TypeOf(Func<Path, JsonSchemaSubset.Reading> read, Action<string> problem)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(problem);

        return Typed(new Typing(read, problem));
    }

    /// <summary>
    /// What a read of a path yields to the tree: the value the reader reached, and how many of the path's steps it
    /// walked to reach it - a group's member named after the group's own name is the reader's to find, for only it
    /// knows which instance a name means - so the tree walks only the steps after them.
    /// </summary>
    /// <param name="Value">What the reader reached: the root's value, or the member's it walked down to.</param>
    /// <param name="Walked">How many of the path's first steps the reader consumed reaching it, all of them keys.</param>
    public readonly record struct PathRead(FlowValue Value, int Walked);

    /// <summary>
    /// What this expression yields as a run reads it, over what each path's root yields then: the same operators, with
    /// the same short-circuits, evaluated left to right. A value an operator does not take - a Missing name where a value
    /// was needed - throws <see cref="UnresolvedReadException"/>, and the step that read it failed with 12.
    /// </summary>
    /// <param name="read">
    /// What a path's root yields when the run reads it - a step's output or one of its facts, an input, one of the run's
    /// own names - answering <see cref="FlowValue.Missing"/> where nothing holds it; the tree walks every step after the
    /// root. A path from <c>it</c> is read here, as an item of the list the nearest <c>any(...)</c> looks into.
    /// </param>
    public FlowValue Evaluate(Func<Path, FlowValue> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        return Evaluate(path => new PathRead(read(path), 0));
    }

    /// <summary>
    /// What this expression yields as a run reads it, over what each path's reader reached: the reader may walk the
    /// first keys of a path itself - a group's member read from outside it - and says how many, and the tree walks the
    /// rest. Otherwise as <see cref="Evaluate(Func{Path, FlowValue})"/>.
    /// </summary>
    /// <param name="read">What a path's reader reached, and how many of its steps it walked.</param>
    public FlowValue Evaluate(Func<Path, PathRead> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        return Yield(new Evaluating(read));
    }

    /// <summary>What this node yields, given what its paths yield and the items the <c>any(...)</c> calls around it look into.</summary>
    private protected abstract FlowValue Yield(Evaluating evaluating);

    /// <summary>
    /// One reading of one expression at run time: what its paths yield, and the items the <c>any(...)</c> calls around
    /// the part being read look into, the nearest last.
    /// </summary>
    private protected sealed record Evaluating(Func<Path, PathRead> Read)
    {
        /// <summary>Each <c>any(...)</c> around the part being read: the item its test names <c>it</c>.</summary>
        public Stack<FlowValue> Items { get; } = new();
    }

    /// <summary>
    /// The expression as it reads, rebuilt from the tree: spaced as this class writes it, and parenthesized only where
    /// the grammar needs it, so a refusal quotes a part of it as one would write it.
    /// </summary>
    public abstract string Written { get; }

    /// <summary>How tightly this node binds, loosest first: a part written inside a tighter one is parenthesized.</summary>
    private protected virtual int Binding => Primary;

    /// <summary>The binding of a primary - a path, a literal, a placeholder, a call - which nothing parenthesizes.</summary>
    private const int Primary = 6;

    /// <summary><paramref name="part"/> written inside a node that takes parts binding at least <paramref name="least"/>.</summary>
    private static string Inside(FlowExpression part, int least) => part.Binding < least ? $"({part.Written})" : part.Written;

    /// <summary>What this node yields, given what its paths yield, saying each misuse as it is found.</summary>
    private protected abstract JsonSchemaSubset.Reading Typed(Typing typing);

    /// <summary>
    /// One typing of one expression: what its paths yield, where its misuses go, and the items the <c>any(...)</c>
    /// calls around the part being typed look into, the nearest last.
    /// </summary>
    private protected sealed record Typing(Func<Path, JsonSchemaSubset.Reading> Read, Action<string> Problem)
    {
        /// <summary>Each <c>any(...)</c> around the part being typed: what an item of its list is, and the list as written.</summary>
        public Stack<(JsonSchemaSubset.Reading Item, string List)> Items { get; } = new();
    }

    /// <summary>A boolean, as every comparison, every test and every join yields.</summary>
    private static JsonSchemaSubset.Reading Boolean => JsonSchemaSubset.Reading.Of("boolean");

    /// <summary>
    /// Says <paramref name="part"/> is no boolean where it can be none, as <paramref name="what"/> words the misuse
    /// around it: nothing is read as true or as false but <c>true</c> and <c>false</c>.
    /// </summary>
    private static void HeldToABoolean(FlowExpression part, Typing typing, Func<string, string> what)
    {
        var reading = part.Typed(typing);

        if (!reading.MayBe("boolean"))
        {
            typing.Problem(what(Said(reading)));
        }
    }

    /// <summary>
    /// The kinds <paramref name="reading"/> may be, in words: <c>a text</c>, <c>a list or an object</c>, <c>a whole
    /// number or null</c> - a number standing for a whole number too, as the subset reads one.
    /// </summary>
    /// <param name="reading">What a value is said to be; its kinds known.</param>
    internal static string Said(JsonSchemaSubset.Reading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);

        var kinds = reading.Kinds ?? [];
        var number = kinds.Contains("number", StringComparer.Ordinal);
        var words = kinds
            .Where(kind => !(number && kind == "integer"))
            .Select(kind => kind switch
            {
                "object" => "an object",
                "array" => "a list",
                "string" => "a text",
                "integer" => "a whole number",
                "number" => "a number",
                "boolean" => "a boolean",
                _ => "null",
            })
            .ToList();

        return words.Count switch
        {
            0 => "nothing",
            1 => words[0],
            _ => $"{string.Join(", ", words.Take(words.Count - 1))} or {words[^1]}",
        };
    }

    /// <summary>
    /// What <paramref name="steps"/> walk to from <paramref name="start"/>, a value written <paramref name="written"/>:
    /// each key a member of the value so far, each index an item of it. <see langword="null"/> where a step finds
    /// nothing a value its reading allows could hold, and <paramref name="why"/> says it, as what follows the owner of
    /// the reading: <c>has no property 'itemz'</c>, <c>says 'plan.summary' is a text, which has no property 'x'</c>.
    /// </summary>
    /// <param name="start">What the value the walk starts from is said to be.</param>
    /// <param name="written">The value the walk starts from, as the read writes it.</param>
    /// <param name="steps">The keys and indexes walked, in order.</param>
    /// <param name="why">Why the walk found nothing; <see langword="null"/> where it found something.</param>
    internal static JsonSchemaSubset.Reading? Walk(JsonSchemaSubset.Reading start, string written, IEnumerable<Path.Segment> steps, out string? why)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(steps);

        var reading = start;
        var at = written;

        foreach (var step in steps)
        {
            if (step.Key is { } key)
            {
                if (!reading.MayBe("object"))
                {
                    why = $"says '{at}' is {Said(reading)}, which has no property '{key}'";
                    return null;
                }

                if (reading.Member(key) is not { } member)
                {
                    why = at == written ? $"has no property '{key}'" : $"has no property '{key}' in '{at}'";
                    return null;
                }

                reading = member;
                at = $"{at}.{key}";
                continue;
            }

            if (!reading.MayBe("array"))
            {
                why = $"says '{at}' is {Said(reading)}, which is no list to index";
                return null;
            }

            reading = reading.Items ?? JsonSchemaSubset.Reading.Anything;
            at = $"{at}[{step.Index}]";
        }

        why = null;
        return reading;
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
        /// <inheritdoc/>
        public override string Written => string.Join(" || ", Terms.Select(term => Inside(term, 2)));

        private protected override int Binding => 1;

        protected internal override void Collect(List<WorkflowExpressions.Read> reads, bool insideAny)
        {
            foreach (var term in Terms)
            {
                term.Collect(reads, insideAny);
            }
        }

        private protected override JsonSchemaSubset.Reading Typed(Typing typing)
        {
            foreach (var term in Terms)
            {
                HeldToABoolean(term, typing, said => $"joins '{term.Written}', which is {said}, with ||; || joins booleans, and nothing is read as true or as false but true and false");
            }

            return Boolean;
        }

        private protected override FlowValue Yield(Evaluating evaluating)
        {
            foreach (var term in Terms)
            {
                // A true term settles the join; the terms after it are not read, which is the short-circuit.
                if (term.Yield(evaluating).AsBoolean(term.Written))
                {
                    return FlowValue.Of(true);
                }
            }

            return FlowValue.Of(false);
        }
    }

    /// <summary><c>a &amp;&amp; b</c>: true where every term is true, short-circuiting left to right.</summary>
    public sealed record And(IReadOnlyList<FlowExpression> Terms) : FlowExpression
    {
        /// <inheritdoc/>
        public override string Written => string.Join(" && ", Terms.Select(term => Inside(term, 3)));

        private protected override int Binding => 2;

        protected internal override void Collect(List<WorkflowExpressions.Read> reads, bool insideAny)
        {
            foreach (var term in Terms)
            {
                term.Collect(reads, insideAny);
            }
        }

        private protected override JsonSchemaSubset.Reading Typed(Typing typing)
        {
            foreach (var term in Terms)
            {
                HeldToABoolean(term, typing, said => $"joins '{term.Written}', which is {said}, with &&; && joins booleans, and nothing is read as true or as false but true and false");
            }

            return Boolean;
        }

        private protected override FlowValue Yield(Evaluating evaluating)
        {
            foreach (var term in Terms)
            {
                // A false term settles the join; the terms after it are not read, which is the short-circuit.
                if (!term.Yield(evaluating).AsBoolean(term.Written))
                {
                    return FlowValue.Of(false);
                }
            }

            return FlowValue.Of(true);
        }
    }

    /// <summary><c>a == b</c> or <c>a != b</c>: two scalars, or null, compared for equality or its opposite.</summary>
    public sealed record Compare(FlowExpression Left, string Operator, FlowExpression Right) : FlowExpression
    {
        /// <inheritdoc/>
        public override string Written => $"{Inside(Left, 4)} {Operator} {Inside(Right, 4)}";

        private protected override int Binding => 3;

        protected internal override void Collect(List<WorkflowExpressions.Read> reads, bool insideAny)
        {
            Left.Collect(reads, insideAny);
            Right.Collect(reads, insideAny);
        }

        /// <remarks>
        /// Two scalars of different kinds are unequal, never an error, and null equals only null; a side that can be
        /// nothing but a list or an object is refused, since no scalar is ever one.
        /// </remarks>
        private protected override JsonSchemaSubset.Reading Typed(Typing typing)
        {
            foreach (var side in new[] { Left, Right })
            {
                var reading = side.Typed(typing);

                if (reading.Kinds is { Count: > 0 } kinds && kinds.All(kind => kind is "array" or "object"))
                {
                    typing.Problem($"compares '{side.Written}', which is {Said(reading)}, with {Operator}; {Operator} compares two scalars, or null, and a list or an object is never one");
                }
            }

            return Boolean;
        }

        private protected override FlowValue Yield(Evaluating evaluating)
        {
            var left = Left.Yield(evaluating).AsScalar(Left.Written);
            var right = Right.Yield(evaluating).AsScalar(Right.Written);
            var equal = FlowValue.ScalarsEqual(left, right);

            return FlowValue.Of(Operator == "==" ? equal : !equal);
        }
    }

    /// <summary><c>!a</c>: the opposite of a boolean.</summary>
    public sealed record Not(FlowExpression Inner) : FlowExpression
    {
        /// <inheritdoc/>
        public override string Written => $"!{Inside(Inner, 4)}";

        private protected override int Binding => 4;

        protected internal override void Collect(List<WorkflowExpressions.Read> reads, bool insideAny)
            => Inner.Collect(reads, insideAny);

        private protected override JsonSchemaSubset.Reading Typed(Typing typing)
        {
            HeldToABoolean(Inner, typing, said => $"applies ! to '{Inner.Written}', which is {said}; ! takes a boolean, and nothing is read as true or as false but true and false");
            return Boolean;
        }

        private protected override FlowValue Yield(Evaluating evaluating)
            => FlowValue.Of(!Inner.Yield(evaluating).AsBoolean(Inner.Written));
    }

    /// <summary><c>a ?? b</c>: the left side where it is there and not null, the right otherwise.</summary>
    public sealed record Coalesce(IReadOnlyList<FlowExpression> Terms) : FlowExpression
    {
        /// <inheritdoc/>
        public override string Written => string.Join(" ?? ", Terms.Select(term => Inside(term, Primary)));

        private protected override int Binding => 5;

        protected internal override void Collect(List<WorkflowExpressions.Read> reads, bool insideAny)
        {
            foreach (var term in Terms)
            {
                term.Collect(reads, insideAny);
            }
        }

        /// <remarks>Takes anything: it yields any side but the last where that side is not null, and the last as it is.</remarks>
        private protected override JsonSchemaSubset.Reading Typed(Typing typing)
        {
            JsonSchemaSubset.Reading? yielded = null;

            foreach (var (term, index) in Terms.Select((term, index) => (term, index)))
            {
                var reading = term.Typed(typing);
                var given = index < Terms.Count - 1 ? reading.Without("null") : reading;

                yielded = yielded is null ? given : yielded.Or(given);
            }

            return yielded!;
        }

        private protected override FlowValue Yield(Evaluating evaluating)
        {
            // The first term that is there and not null settles it: Missing and null both fall through, and the last
            // term is yielded as it is.
            for (var index = 0; index < Terms.Count; index++)
            {
                var value = Terms[index].Yield(evaluating);

                if (index < Terms.Count - 1 && (value.IsMissing || value.IsNull))
                {
                    continue;
                }

                return value;
            }

            return FlowValue.Missing;
        }
    }

    /// <summary><c>empty(x)</c>: true for null, <c>""</c>, <c>[]</c>, <c>{}</c> and a name that reads nothing.</summary>
    public sealed record Empty(FlowExpression Inner) : FlowExpression
    {
        /// <inheritdoc/>
        public override string Written => $"empty({Inner.Written})";

        protected internal override void Collect(List<WorkflowExpressions.Read> reads, bool insideAny)
        {
            // The scanner reads the word itself, as a root of the run's own: the tree reads it too, so the two
            // agree on every expression.
            Add(reads, new WorkflowExpressions.Read("empty", "empty", Template: false, insideAny));
            Inner.Collect(reads, insideAny);
        }

        /// <remarks>Takes anything, and says whether it holds nothing.</remarks>
        private protected override JsonSchemaSubset.Reading Typed(Typing typing)
        {
            _ = Inner.Typed(typing);
            return Boolean;
        }

        private protected override FlowValue Yield(Evaluating evaluating)
            => FlowValue.Of(Inner.Yield(evaluating).HoldsNothing);
    }

    /// <summary>
    /// <c>any(list, test)</c>: true where <c>test</c> is true of at least one member of <c>list</c>, the member
    /// named <c>it</c> inside the test.
    /// </summary>
    public sealed record Any(FlowExpression List, FlowExpression Test) : FlowExpression
    {
        /// <inheritdoc/>
        public override string Written => $"any({List.Written}, {Test.Written})";

        protected internal override void Collect(List<WorkflowExpressions.Read> reads, bool insideAny)
        {
            // The scanner marks every read within an any(...) call, its list as its test: the tree reads it the
            // same, so the two agree on every expression, wherever the member's own name binds.
            List.Collect(reads, insideAny: true);
            Test.Collect(reads, insideAny: true);
        }

        private protected override JsonSchemaSubset.Reading Typed(Typing typing)
        {
            var list = List.Typed(typing);

            if (!list.MayBe("array"))
            {
                typing.Problem($"looks into '{List.Written}' with any, and it is {Said(list)}; any looks into a list");
            }

            typing.Items.Push((list.Items ?? JsonSchemaSubset.Reading.Anything, List.Written));
            HeldToABoolean(Test, typing, said => $"tests each item of '{List.Written}' with '{Test.Written}', which is {said}; the test of any yields a boolean for each item");
            typing.Items.Pop();

            return Boolean;
        }

        private protected override FlowValue Yield(Evaluating evaluating)
        {
            var list = List.Yield(evaluating).AsList(List.Written);

            foreach (var item in list)
            {
                evaluating.Items.Push(FlowValue.Of(item));

                try
                {
                    if (Test.Yield(evaluating).AsBoolean(Test.Written))
                    {
                        return FlowValue.Of(true);
                    }
                }
                finally
                {
                    evaluating.Items.Pop();
                }
            }

            return FlowValue.Of(false);
        }
    }

    /// <summary>A literal as written: <c>true</c>, <c>false</c>, <c>null</c>, a number, or a quoted string.</summary>
    public sealed record Literal(object? Value) : FlowExpression
    {
        /// <inheritdoc/>
        public override string Written => Value switch
        {
            null => "null",
            true => "true",
            false => "false",
            string text => text.Contains('\'', StringComparison.Ordinal) ? $"\"{text}\"" : $"'{text}'",
            IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
            _ => Value.ToString() ?? string.Empty,
        };

        private protected override JsonSchemaSubset.Reading Typed(Typing typing)
            => JsonSchemaSubset.Reading.Of(Value switch
            {
                null => "null",
                bool => "boolean",
                long => "integer",
                double => "number",
                _ => "string",
            });

        private protected override FlowValue Yield(Evaluating evaluating) => FlowValue.Of(Value);
    }

    /// <summary>A <c>{path}</c> placeholder read inside an expression: a read of its own, filled as templates are.</summary>
    public sealed record Placeholder(string Whole) : FlowExpression
    {
        /// <inheritdoc/>
        public override string Written => $"{{{Whole}}}";

        protected internal override void Collect(List<WorkflowExpressions.Read> reads, bool insideAny)
            => Add(reads, Read(Whole, Template: true, insideAny));

        /// <remarks>Filled as a template is, its value written as text whatever it reads.</remarks>
        private protected override JsonSchemaSubset.Reading Typed(Typing typing) => JsonSchemaSubset.Reading.Of("string");

        private protected override FlowValue Yield(Evaluating evaluating)
        {
            // A placeholder names one value by its path, read as every path is - its first name the root, each name
            // after it walked - and what it yields is written as text, as a template fills it.
            var value = Named().Yield(evaluating);

            if (value.IsMissing)
            {
                throw new UnresolvedReadException($"{{{Whole}}} reads nothing");
            }

            return FlowValue.Of(value.AsTemplateText());
        }

        /// <summary>The path the placeholder names, parsed by the grammar's own rule for one.</summary>
        private Path Named()
            => GrammarProblem(Whole) is { } problem
                ? throw new UnresolvedReadException($"{{{Whole}}} names no path: {problem}")
                : Parse(Whole) as Path ?? throw new UnresolvedReadException($"{{{Whole}}} names no path");
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

        /// <inheritdoc/>
        public override string Written => Whole;

        protected internal override void Collect(List<WorkflowExpressions.Read> reads, bool insideAny)
            => Add(reads, Read(Whole, Template: false, insideAny));

        /// <remarks>
        /// A path from <c>it</c> walks an item of the list the nearest <c>any(...)</c> looks into; every other path is
        /// the caller's to say. Outside every <c>any(...)</c>, <c>it</c> names nothing, which its read's own check
        /// refuses.
        /// </remarks>
        private protected override JsonSchemaSubset.Reading Typed(Typing typing)
        {
            if (Root != "it")
            {
                return typing.Read(this);
            }

            if (!typing.Items.TryPeek(out var around))
            {
                return JsonSchemaSubset.Reading.Anything;
            }

            if (Walk(around.Item, Root, Steps, out var why) is { } walked)
            {
                return walked;
            }

            typing.Problem($"reads '{Whole}', and each item of '{around.List}' {why}");
            return JsonSchemaSubset.Reading.Anything;
        }

        private protected override FlowValue Yield(Evaluating evaluating)
        {
            var read = Root == "it"
                ? evaluating.Items.TryPeek(out var item)
                    ? new PathRead(item, 0)
                    : throw new UnresolvedReadException($"'it' names the member of the list any looks into, and there is none here")
                : evaluating.Read(this);

            if (read.Walked < 0 || read.Walked > Steps.Count || Steps.Take(read.Walked).Any(step => !step.IsKey))
            {
                throw new InvalidOperationException($"The reader of '{Whole}' says it walked {read.Walked} of its {Steps.Count} steps, and those are not keys of it.");
            }

            // Each step the reader did not walk walks what the last one named; a step that finds nothing leaves the value
            // Missing, which ?? and empty(...) take and nothing else does.
            var value = read.Value;

            foreach (var step in Steps.Skip(read.Walked))
            {
                value = step.Key is { } key ? value.Member(key) : value.At(step.Index ?? 0);
            }

            return value;
        }
    }

    /// <summary>One read as the scanner spells it: root, whole name, template or not, and whether it sits in an <c>any</c> test.</summary>
    private static WorkflowExpressions.Read Read(string whole, bool Template, bool insideAny)
        => WorkflowExpressions.ReadOf(whole, Template, insideAny);

    /// <summary>The recursive-descent reader of the grammar, over the text as written.</summary>
    private sealed class Parser(string text)
    {
        private readonly string _text = text;

        /// <summary>How deep the reader is: each construct inside another, and each name of a path after its first.</summary>
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
        {
            if (!Takes("!"))
            {
                return Coalesce();
            }

            Deeper();
            var inner = Not();
            _depth--;
            return new Not(inner);
        }

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
                Deeper();
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
                    Deeper();
                    var inner = Or();
                    Expect(')');
                    _depth--;
                    return new Empty(inner);
                }

                if (string.Equals(name, "any", StringComparison.Ordinal))
                {
                    Expect('(');
                    Deeper();
                    var list = Or();
                    Expect(',');
                    var test = Or();
                    Expect(')');
                    _depth--;
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

        /// <summary>
        /// path = root { "." key | "[" int "]" }, the root already read: written whole, so a dot or a bracket is part
        /// of the path only where it follows the name with no space between.
        /// </summary>
        private FlowExpression Walk(string root)
        {
            var steps = new List<Path.Segment>();

            while (true)
            {
                var at = At;

                if (at < _text.Length && _text[at] == '.' && at + 1 < _text.Length && StartsName(_text[at + 1]))
                {
                    Deeper();
                    var (key, next) = Name(at + 1);
                    steps.Add(new Path.Segment(key, null));
                    At = next;
                }
                else if (at < _text.Length && _text[at] == '[')
                {
                    Deeper();
                    var close = _text.IndexOf(']', at + 1);

                    if (close < 0 || WorkflowExpressions.WholeNumber(_text[(at + 1)..close]) is not { } index)
                    {
                        throw new ArgumentException($"the index at column {at + 1} is not a whole number from 0, written in digits alone with no leading zero", Text);
                    }

                    steps.Add(new Path.Segment(null, index));
                    At = close + 1;
                }
                else
                {
                    // A path's names went one deeper each; past the path the reader is as deep as before it.
                    _depth -= steps.Count;
                    return new Path(root, steps);
                }
            }
        }

        /// <summary>Goes one deeper, refusing past <see cref="MostNesting"/>: the one bound on every recursion of the reader and of the tree it builds.</summary>
        private void Deeper()
        {
            if (++_depth > MostNesting)
            {
                throw new ArgumentException($"the expression nests more than {MostNesting} deep", Text);
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
