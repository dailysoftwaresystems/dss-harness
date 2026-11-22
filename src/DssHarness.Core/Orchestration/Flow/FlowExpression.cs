using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
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
/// }</c>, and a primary is a group, <c>empty(...)</c>, <c>any(list, test)</c>, <c>union(list, ...)</c>, a literal, a
/// <c>{path}</c> placeholder or a path. A path is a root - a step's name, <c>previous</c> with a step's name, one of the run's
/// own names - walked by keys and integer indexes, and written whole: no space stands before its dots or its
/// brackets, so <c>review .findings</c> is two values side by side and refused, never one path. Identifiers are
/// letters, underscores, digits and dashes, so a step's name needs no quoting; there is no arithmetic, for
/// expressions select and never compute.
/// </para>
/// <para>
/// An expression nests as deep as it is written, and no depth is refused: the reader keeps the rules it is inside on a
/// stack of its own, and the tree is written, read for its names, typed and evaluated the same way - each a walk on a
/// stack of its own, never a call per level - so a condition a generator nests ten thousand deep is read whole, and no
/// text a file holds can spend the stack of the process that reads it.
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
        var expression = parser.Expression();

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

    /// <summary>
    /// Every name this expression reads, in the order it reads them, once each: the tree's own account, agreeing with
    /// <see cref="WorkflowExpressions.ReadsOf"/>. The tree is gone down on a stack of this walk's own, each node's parts
    /// in the order they are written, and every name already read is kept, so a read written again is passed over.
    /// </summary>
    public IReadOnlyList<WorkflowExpressions.Read> Reads()
    {
        var reads = new List<WorkflowExpressions.Read>();
        var read = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<(FlowExpression Node, bool InsideAny)>();

        void Add(WorkflowExpressions.Read named)
        {
            if (read.Add(named.Whole))
            {
                reads.Add(named);
            }
        }

        pending.Push((this, false));

        while (pending.TryPop(out var at))
        {
            switch (at.Node)
            {
                case Path path:
                    Add(Read(path.Whole, Template: false, at.InsideAny));
                    continue;

                case Placeholder placeholder:
                    Add(Read(placeholder.Whole, Template: true, at.InsideAny));
                    continue;

                // The scanner reads each function's word itself, as a root of the run's own: the tree reads it too, so
                // the two agree on every expression.
                case Empty:
                    Add(new WorkflowExpressions.Read("empty", "empty", Template: false, at.InsideAny));
                    break;

                case Union:
                    Add(new WorkflowExpressions.Read("union", "union", Template: false, at.InsideAny));
                    break;
            }

            // The scanner marks every read within an any(...) call, its list as its test: the tree reads it the same,
            // so the two agree on every expression, wherever the member's own name binds.
            var insideAny = at.InsideAny || at.Node is Any;
            var parts = PartsOf(at.Node);

            for (var index = parts.Count - 1; index >= 0; index--)
            {
                pending.Push((parts[index], insideAny));
            }
        }

        return reads;
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

        return Typing.Of(this, read, problem);
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

        return Evaluation.Of(this, read);
    }

    /// <summary>
    /// The expression as it reads, rebuilt from the tree: spaced as this class writes it, and parenthesized only where
    /// the grammar needs it, so a refusal quotes a part of it as one would write it. Written on a stack of the writer's
    /// own, in time linear in what it writes.
    /// </summary>
    public string Written => Writing.Of(this);

    /// <summary>The binding of a primary - a path, a literal, a placeholder, a call - which nothing parenthesizes.</summary>
    private const int Primary = 6;

    /// <summary>How tightly <paramref name="node"/> binds, loosest first: a part written inside a tighter one is parenthesized.</summary>
    private static int BindingOf(FlowExpression node) => node switch
    {
        Or => 1,
        And => 2,
        Compare => 3,
        Not => 4,
        Coalesce => 5,
        _ => Primary,
    };

    /// <summary>The parts <paramref name="node"/> joins, in the order they are written, read, typed and evaluated; none for a leaf.</summary>
    private static IReadOnlyList<FlowExpression> PartsOf(FlowExpression node) => node switch
    {
        Or or => or.Terms,
        And and => and.Terms,
        Compare compare => [compare.Left, compare.Right],
        Not not => [not.Inner],
        Coalesce coalesce => coalesce.Terms,
        Empty empty => [empty.Inner],
        Any any => [any.List, any.Test],
        Union union => union.Lists,
        _ => [],
    };

    /// <summary>A boolean, as every comparison, every test and every join yields.</summary>
    private static JsonSchemaSubset.Reading Boolean => JsonSchemaSubset.Reading.Of("boolean");

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
        var at = new StringBuilder(written);

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
                    why = at.Length == written.Length ? $"has no property '{key}'" : $"has no property '{key}' in '{at}'";
                    return null;
                }

                reading = member;
                at.Append('.').Append(key);
                continue;
            }

            if (!reading.MayBe("array"))
            {
                why = $"says '{at}' is {Said(reading)}, which is no list to index";
                return null;
            }

            reading = reading.Items ?? JsonSchemaSubset.Reading.Anything;
            at.Append('[').Append(step.Index?.ToString(CultureInfo.InvariantCulture)).Append(']');
        }

        why = null;
        return reading;
    }

    /// <summary><c>a || b || c</c>: true where any term is true, short-circuiting left to right.</summary>
    public sealed record Or(IReadOnlyList<FlowExpression> Terms) : FlowExpression;

    /// <summary><c>a &amp;&amp; b</c>: true where every term is true, short-circuiting left to right.</summary>
    public sealed record And(IReadOnlyList<FlowExpression> Terms) : FlowExpression;

    /// <summary>
    /// <c>a == b</c> or <c>a != b</c>: two scalars, or null, compared for equality or its opposite. Two scalars of
    /// different kinds are unequal, never an error, and null equals only null; a side that can be nothing but a list or
    /// an object is refused, since no scalar is ever one.
    /// </summary>
    public sealed record Compare(FlowExpression Left, string Operator, FlowExpression Right) : FlowExpression;

    /// <summary><c>!a</c>: the opposite of a boolean.</summary>
    public sealed record Not(FlowExpression Inner) : FlowExpression;

    /// <summary>
    /// <c>a ?? b</c>: the left side where it is there and not null, the right otherwise. Takes anything: it yields any
    /// side but the last where that side is not null, and the last as it is.
    /// </summary>
    public sealed record Coalesce(IReadOnlyList<FlowExpression> Terms) : FlowExpression;

    /// <summary><c>empty(x)</c>: true for null, <c>""</c>, <c>[]</c>, <c>{}</c> and a name that reads nothing. Takes anything, and says whether it holds nothing.</summary>
    public sealed record Empty(FlowExpression Inner) : FlowExpression;

    /// <summary>
    /// <c>any(list, test)</c>: true where <c>test</c> is true of at least one member of <c>list</c>, the member
    /// named <c>it</c> inside the test.
    /// </summary>
    public sealed record Any(FlowExpression List, FlowExpression Test) : FlowExpression;

    /// <summary>
    /// <c>union(a, b, ...)</c>: the members of every list it is given, in the order given, each once - two members are
    /// one where they are the same JSON. A list given as null holds no member; anything else it is given - nothing, a
    /// text, an object - refuses the read, for union joins lists. Each part a list, or null: a list whose members are
    /// what any of them may hold.
    /// </summary>
    public sealed record Union(IReadOnlyList<FlowExpression> Lists) : FlowExpression;

    /// <summary>A literal as written: <c>true</c>, <c>false</c>, <c>null</c>, a number, or a quoted string.</summary>
    public sealed record Literal(object? Value) : FlowExpression
    {
        /// <summary>The literal as the expression writes it.</summary>
        internal string Text => Value switch
        {
            null => "null",
            true => "true",
            false => "false",
            string text => text.Contains('\'', StringComparison.Ordinal) ? $"\"{text}\"" : $"'{text}'",
            IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
            _ => Value.ToString() ?? string.Empty,
        };

        /// <summary>What the literal is, as the definition is typed.</summary>
        internal JsonSchemaSubset.Reading Reading
            => JsonSchemaSubset.Reading.Of(Value switch
            {
                null => "null",
                bool => "boolean",
                long => "integer",
                double => "number",
                _ => "string",
            });
    }

    /// <summary>
    /// A <c>{path}</c> placeholder read inside an expression: a read of its own, filled as templates are - its value
    /// written as text whatever it reads.
    /// </summary>
    public sealed record Placeholder(string Whole) : FlowExpression
    {
        /// <summary>
        /// What the placeholder yields: the one value its path names, read as every path is - its first name the root,
        /// each name after it walked - written as text, as a template fills it.
        /// </summary>
        internal FlowValue Yield(Func<Path, PathRead> read, Stack<FlowValue> items)
        {
            var value = Named().Yield(read, items);

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
        public string Whole
        {
            get
            {
                var whole = new StringBuilder(Root);

                foreach (var step in Steps)
                {
                    _ = step.IsKey ? whole.Append('.').Append(step.Key) : whole.Append('[').Append(step.Index?.ToString(CultureInfo.InvariantCulture)).Append(']');
                }

                return whole.ToString();
            }
        }

        /// <summary>
        /// What the path is, as the definition is typed. A path from <c>it</c> walks an item of the list the nearest
        /// <c>any(...)</c> looks into; every other path is the caller's to say. Outside every <c>any(...)</c>, <c>it</c>
        /// names nothing, which its read's own check refuses.
        /// </summary>
        internal JsonSchemaSubset.Reading Typed(Func<Path, JsonSchemaSubset.Reading> read, Action<string> problem, Stack<(JsonSchemaSubset.Reading Item, FlowExpression List)> items)
        {
            if (Root != "it")
            {
                return read(this);
            }

            if (!items.TryPeek(out var around))
            {
                return JsonSchemaSubset.Reading.Anything;
            }

            if (Walk(around.Item, Root, Steps, out var why) is { } walked)
            {
                return walked;
            }

            problem($"reads '{Whole}', and each item of '{around.List.Written}' {why}");
            return JsonSchemaSubset.Reading.Anything;
        }

        /// <summary>What the path yields as a run reads it: its root's value, or the item of the nearest <c>any(...)</c> for <c>it</c>, walked by every step its reader did not walk.</summary>
        internal FlowValue Yield(Func<Path, PathRead> read, Stack<FlowValue> items)
        {
            var reached = Root == "it"
                ? items.TryPeek(out var item)
                    ? new PathRead(item, 0)
                    : throw new UnresolvedReadException($"'it' names the member of the list any looks into, and there is none here")
                : read(this);

            if (reached.Walked < 0 || reached.Walked > Steps.Count || Steps.Take(reached.Walked).Any(step => !step.IsKey))
            {
                throw new InvalidOperationException($"The reader of '{Whole}' says it walked {reached.Walked} of its {Steps.Count} steps, and those are not keys of it.");
            }

            // Each step the reader did not walk walks what the last one named; a step that finds nothing leaves the value
            // Missing, which ?? and empty(...) take and nothing else does.
            var value = reached.Value;

            for (var at = reached.Walked; at < Steps.Count; at++)
            {
                var step = Steps[at];
                value = step.Key is { } key ? value.Member(key) : value.At(step.Index ?? 0);
            }

            return value;
        }
    }

    /// <summary>One read as the scanner spells it: root, whole name, template or not, and whether it sits in an <c>any</c> test.</summary>
    private static WorkflowExpressions.Read Read(string whole, bool Template, bool insideAny)
        => WorkflowExpressions.ReadOf(whole, Template, insideAny);

    /// <summary>
    /// The writer of a tree back to its text: each node's pieces - its parts and the words between them - pushed on a
    /// stack of the writer's own, last first, so the text comes out in order whatever the depth.
    /// </summary>
    private static class Writing
    {
        /// <summary><paramref name="root"/> written as the grammar spells it.</summary>
        public static string Of(FlowExpression root)
        {
            var text = new StringBuilder();
            var pending = new Stack<object>();

            pending.Push(root);

            while (pending.TryPop(out var piece))
            {
                if (piece is string said)
                {
                    _ = text.Append(said);
                    continue;
                }

                var pieces = PiecesOf((FlowExpression)piece);

                for (var index = pieces.Count - 1; index >= 0; index--)
                {
                    pending.Push(pieces[index]);
                }
            }

            return text.ToString();
        }

        /// <summary>What <paramref name="node"/> is written as: words, and parts each written in its turn.</summary>
        private static List<object> PiecesOf(FlowExpression node)
        {
            switch (node)
            {
                case Or or:
                    return Joined(or.Terms, " || ", least: 2);
                case And and:
                    return Joined(and.Terms, " && ", least: 3);
                case Compare compare:
                    return [.. Inside(compare.Left, 4), $" {compare.Operator} ", .. Inside(compare.Right, 4)];
                case Not not:
                    return ["!", .. Inside(not.Inner, 4)];
                case Coalesce coalesce:
                    return Joined(coalesce.Terms, " ?? ", least: Primary);
                case Empty empty:
                    return ["empty(", empty.Inner, ")"];
                case Any any:
                    return ["any(", any.List, ", ", any.Test, ")"];
                case Union union:
                    return ["union(", .. Joined(union.Lists, ", ", least: 0), ")"];
                case Literal literal:
                    return [literal.Text];
                case Placeholder placeholder:
                    return [$"{{{placeholder.Whole}}}"];
                case Path path:
                    return [path.Whole];
                default:
                    throw new InvalidOperationException($"An expression holds a node of a kind the writer does not know: {node.GetType().Name}.");
            }
        }

        /// <summary><paramref name="parts"/> joined by <paramref name="between"/>, each inside a node that takes parts binding at least <paramref name="least"/>.</summary>
        private static List<object> Joined(IReadOnlyList<FlowExpression> parts, string between, int least)
        {
            var pieces = new List<object>();

            foreach (var (part, index) in parts.Select((part, index) => (part, index)))
            {
                if (index > 0)
                {
                    pieces.Add(between);
                }

                pieces.AddRange(Inside(part, least));
            }

            return pieces;
        }

        /// <summary><paramref name="part"/> written inside a node that takes parts binding at least <paramref name="least"/>: parenthesized where it binds more loosely.</summary>
        private static object[] Inside(FlowExpression part, int least)
            => BindingOf(part) < least ? ["(", part, ")"] : [part];
    }

    /// <summary>
    /// One typing of one expression: the tree gone down on a stack of frames of its own, each part typed before the node
    /// that joins it weighs it - in the order a part is written, each misuse said as it is met - and the items the
    /// <c>any(...)</c> calls around the part being typed look into, the nearest last.
    /// </summary>
    private sealed class Typing
    {
        private readonly Func<Path, JsonSchemaSubset.Reading> _read;
        private readonly Action<string> _problem;

        /// <summary>Each <c>any(...)</c> around the part being typed: what an item of its list is, and the list as written.</summary>
        private readonly Stack<(JsonSchemaSubset.Reading Item, FlowExpression List)> _items = new();

        private Typing(Func<Path, JsonSchemaSubset.Reading> read, Action<string> problem)
        {
            _read = read;
            _problem = problem;
        }

        /// <summary>What <paramref name="root"/> yields, as far as the definition says, each misuse given to <paramref name="problem"/>.</summary>
        public static JsonSchemaSubset.Reading Of(FlowExpression root, Func<Path, JsonSchemaSubset.Reading> read, Action<string> problem)
            => new Typing(read, problem).Typed(root);

        /// <summary>One node being typed: how many of its parts are typed, and what it has gathered of them.</summary>
        private sealed class Frame(FlowExpression node)
        {
            public FlowExpression Node { get; } = node;

            public IReadOnlyList<FlowExpression> Parts { get; } = PartsOf(node);

            /// <summary>How many of its parts have been handed down to be typed.</summary>
            public int Next { get; set; }

            /// <summary>Whether the last part handed down has been typed and waits to be weighed.</summary>
            public bool Waiting { get; set; }

            /// <summary>What a <c>??</c> yields so far, or what the members of a <c>union</c>'s lists may be.</summary>
            public JsonSchemaSubset.Reading? Gathered { get; set; }

            /// <summary>What an <c>any(...)</c>'s list is, once it is typed.</summary>
            public JsonSchemaSubset.Reading? List { get; set; }
        }

        private JsonSchemaSubset.Reading Typed(FlowExpression root)
        {
            var frames = new Stack<Frame>();
            var reading = JsonSchemaSubset.Reading.Anything;

            frames.Push(new Frame(root));

            while (frames.TryPeek(out var frame))
            {
                if (frame.Waiting)
                {
                    frame.Waiting = false;
                    Weigh(frame, frame.Next - 1, reading);
                }

                if (frame.Next < frame.Parts.Count)
                {
                    // An any(...)'s test is typed for an item of its list, which its 'it' names.
                    if (frame.Node is Any any && frame.Next == 1)
                    {
                        _items.Push((frame.List!.Items ?? JsonSchemaSubset.Reading.Anything, any.List));
                    }

                    frame.Waiting = true;
                    frames.Push(new Frame(frame.Parts[frame.Next++]));
                    continue;
                }

                reading = Ended(frame);
                _ = frames.Pop();
            }

            return reading;
        }

        /// <summary>Weighs the part at <paramref name="index"/> of <paramref name="frame"/>'s node, typed <paramref name="reading"/>, saying a misuse where it is one.</summary>
        private void Weigh(Frame frame, int index, JsonSchemaSubset.Reading reading)
        {
            var part = frame.Parts[index];

            switch (frame.Node)
            {
                case Or:
                    HeldToABoolean(reading, said => $"joins '{part.Written}', which is {said}, with ||; || joins booleans, and nothing is read as true or as false but true and false");
                    break;

                case And:
                    HeldToABoolean(reading, said => $"joins '{part.Written}', which is {said}, with &&; && joins booleans, and nothing is read as true or as false but true and false");
                    break;

                case Compare compare:
                    if (reading.Kinds is { Count: > 0 } kinds && kinds.All(kind => kind is "array" or "object"))
                    {
                        _problem($"compares '{part.Written}', which is {Said(reading)}, with {compare.Operator}; {compare.Operator} compares two scalars, or null, and a list or an object is never one");
                    }

                    break;

                case Not:
                    HeldToABoolean(reading, said => $"applies ! to '{part.Written}', which is {said}; ! takes a boolean, and nothing is read as true or as false but true and false");
                    break;

                case Coalesce coalesce:
                    var given = index < coalesce.Terms.Count - 1 ? reading.Without("null") : reading;
                    frame.Gathered = frame.Gathered is null ? given : frame.Gathered.Or(given);
                    break;

                case Any any when index == 0:
                    if (!reading.MayBe("array"))
                    {
                        _problem($"looks into '{any.List.Written}' with any, and it is {Said(reading)}; any looks into a list");
                    }

                    frame.List = reading;
                    break;

                case Any any:
                    HeldToABoolean(reading, said => $"tests each item of '{any.List.Written}' with '{any.Test.Written}', which is {said}; the test of any yields a boolean for each item");
                    _ = _items.Pop();
                    break;

                case Union:
                    if (!reading.MayBe("array") && !reading.MayBe("null"))
                    {
                        _problem($"joins '{part.Written}', which is {Said(reading)}, with union; union joins lists");
                        break;
                    }

                    var member = reading.Items ?? JsonSchemaSubset.Reading.Anything;
                    frame.Gathered = frame.Gathered is null ? member : frame.Gathered.Or(member);
                    break;
            }
        }

        /// <summary>What <paramref name="frame"/>'s node yields, its parts all weighed.</summary>
        private JsonSchemaSubset.Reading Ended(Frame frame) => frame.Node switch
        {
            Or or And or Compare or Not or Empty or Any => Boolean,
            Coalesce => frame.Gathered!,
            Union => JsonSchemaSubset.Reading.ListOf(frame.Gathered ?? JsonSchemaSubset.Reading.Anything),
            Literal literal => literal.Reading,
            Placeholder => JsonSchemaSubset.Reading.Of("string"),
            Path path => path.Typed(_read, _problem, _items),
            _ => throw new InvalidOperationException($"An expression holds a node of a kind the typing does not know: {frame.Node.GetType().Name}."),
        };

        /// <summary>
        /// Says a part typed <paramref name="reading"/> is no boolean where it can be none, as <paramref name="what"/> words
        /// the misuse around it: nothing is read as true or as false but <c>true</c> and <c>false</c>.
        /// </summary>
        private void HeldToABoolean(JsonSchemaSubset.Reading reading, Func<string, string> what)
        {
            if (!reading.MayBe("boolean"))
            {
                _problem(what(Said(reading)));
            }
        }
    }

    /// <summary>
    /// One reading of one expression at run time: the tree gone down on a stack of frames of its own, left to right, each
    /// part read before the node that joins it weighs it and a join settled by a part leaving the parts after it unread -
    /// the short-circuits - with the items the <c>any(...)</c> calls around the part being read look into, the nearest last.
    /// </summary>
    private sealed class Evaluation
    {
        private readonly Func<Path, PathRead> _read;

        /// <summary>Each <c>any(...)</c> around the part being read: the item its test names <c>it</c>.</summary>
        private readonly Stack<FlowValue> _items = new();

        private Evaluation(Func<Path, PathRead> read)
        {
            _read = read;
        }

        /// <summary>What <paramref name="root"/> yields, each path read through <paramref name="read"/>.</summary>
        public static FlowValue Of(FlowExpression root, Func<Path, PathRead> read) => new Evaluation(read).Yielded(root);

        /// <summary>One node being read: how many of its parts are handed down, and what it has gathered of them.</summary>
        private sealed class Frame(FlowExpression node)
        {
            public FlowExpression Node { get; } = node;

            /// <summary>How many of its parts have been handed down to be read.</summary>
            public int Next { get; set; }

            /// <summary>Whether the last part handed down has been read and waits to be weighed.</summary>
            public bool Waiting { get; set; }

            /// <summary>A comparison's left side, once it is read.</summary>
            public object? Left { get; set; }

            /// <summary>The list an <c>any(...)</c> looks into, once it is read.</summary>
            public JsonArray? Items { get; set; }

            /// <summary>How many of an <c>any(...)</c>'s items its test has been read for.</summary>
            public int Item { get; set; }

            /// <summary>What a <c>union</c> has joined so far.</summary>
            public JsonArray Joined { get; } = [];
        }

        private FlowValue Yielded(FlowExpression root)
        {
            var frames = new Stack<Frame>();
            var value = FlowValue.Missing;

            frames.Push(new Frame(root));

            while (frames.TryPeek(out var frame))
            {
                if (frame.Waiting)
                {
                    frame.Waiting = false;

                    if (Weigh(frame, value) is { } settled)
                    {
                        value = settled;
                        _ = frames.Pop();
                        continue;
                    }
                }

                if (NextOf(frame) is { } part)
                {
                    frame.Waiting = true;
                    frames.Push(new Frame(part));
                    continue;
                }

                value = Ended(frame);
                _ = frames.Pop();
            }

            return value;
        }

        /// <summary>The part of <paramref name="frame"/>'s node to read next, or null where it has read all it reads.</summary>
        private FlowExpression? NextOf(Frame frame)
        {
            switch (frame.Node)
            {
                case Any any:
                    if (frame.Next == 0)
                    {
                        frame.Next = 1;
                        return any.List;
                    }

                    // The test is read once for each item, which its 'it' names while it is read.
                    if (frame.Item < frame.Items!.Count)
                    {
                        _items.Push(FlowValue.Of(frame.Items[frame.Item++]));
                        return any.Test;
                    }

                    return null;

                default:
                    var parts = PartsOf(frame.Node);
                    return frame.Next < parts.Count ? parts[frame.Next++] : null;
            }
        }

        /// <summary>Weighs the part of <paramref name="frame"/>'s node just read, <paramref name="value"/>: what the node yields where that settles it, null where it reads on.</summary>
        private FlowValue? Weigh(Frame frame, FlowValue value)
        {
            switch (frame.Node)
            {
                case Or or:
                    // A true term settles the join; the terms after it are not read, which is the short-circuit.
                    var term = or.Terms[frame.Next - 1];
                    return value.AsBoolean(() => term.Written) ? FlowValue.Of(true) : null;

                case And and:
                    // A false term settles the join; the terms after it are not read, which is the short-circuit.
                    var joined = and.Terms[frame.Next - 1];
                    return value.AsBoolean(() => joined.Written) ? null : FlowValue.Of(false);

                case Compare compare when frame.Next == 1:
                    frame.Left = value.AsScalar(() => compare.Left.Written);
                    return null;

                case Compare compare:
                    var equal = FlowValue.ScalarsEqual(frame.Left, value.AsScalar(() => compare.Right.Written));
                    return FlowValue.Of(compare.Operator == "==" ? equal : !equal);

                case Not not:
                    return FlowValue.Of(!value.AsBoolean(() => not.Inner.Written));

                case Coalesce coalesce:
                    // The first term that is there and not null settles it: Missing and null both fall through, and the
                    // last term is yielded as it is.
                    return frame.Next < coalesce.Terms.Count && (value.IsMissing || value.IsNull) ? null : value;

                case Empty:
                    return FlowValue.Of(value.HoldsNothing);

                case Any any when frame.Items is null:
                    frame.Items = value.AsList(() => any.List.Written);
                    return null;

                case Any any:
                    _ = _items.Pop();
                    return value.AsBoolean(() => any.Test.Written) ? FlowValue.Of(true) : null;

                case Union union:
                    if (value.IsNull)
                    {
                        return null;
                    }

                    if (value.ToNode() is not JsonArray members)
                    {
                        throw new UnresolvedReadException($"'{union.Lists[frame.Next - 1].Written}' is {value.InWords()}, and union joins lists");
                    }

                    foreach (var member in members)
                    {
                        if (!frame.Joined.Any(held => JsonNode.DeepEquals(held, member)))
                        {
                            frame.Joined.Add(member?.DeepClone());
                        }
                    }

                    return null;

                default:
                    return null;
            }
        }

        /// <summary>What <paramref name="frame"/>'s node yields once it has read all it reads and nothing settled it.</summary>
        private FlowValue Ended(Frame frame) => frame.Node switch
        {
            Or => FlowValue.Of(false),
            And => FlowValue.Of(true),
            Any => FlowValue.Of(false),
            Coalesce => FlowValue.Missing,
            Union => FlowValue.Of(frame.Joined),
            Literal literal => FlowValue.Of(literal.Value),
            Placeholder placeholder => placeholder.Yield(_read, _items),
            Path path => path.Yield(_read, _items),
            _ => throw new InvalidOperationException($"An expression holds a node of a kind the evaluation does not know: {frame.Node.GetType().Name}."),
        };
    }

    /// <summary>
    /// The reader of the grammar, over the text as written: each rule it is inside kept on a stack of its own - a rule
    /// asks for the rule it reads next, and takes what that rule read when it ends - never a call per level, so an
    /// expression nested however deep is read, and refused only for what the grammar does not say.
    /// </summary>
    private sealed class Parser(string text)
    {
        private readonly string _text = text;

        /// <summary>The parameter a refusal names.</summary>
        private const string Text = nameof(text);

        /// <summary>Where the reader sits.</summary>
        public int At { get; private set; }

        /// <summary>Whether the whole text has been read.</summary>
        public bool Done => Skip() >= _text.Length;

        /// <summary>Where the reader sits, past any space: the column a refusal names.</summary>
        public int Stuck => Skip();

        /// <summary>The rules of the grammar, as the reader keeps them on its stack.</summary>
        private enum Kind
        {
            /// <summary>or = and { "||" and }</summary>
            Or,

            /// <summary>and = cmp { "&amp;&amp;" cmp }</summary>
            And,

            /// <summary>cmp = not [ ("==" | "!=") not ]</summary>
            Cmp,

            /// <summary>not = "!" not | coalesce</summary>
            Not,

            /// <summary>coalesce = primary { "??" primary }</summary>
            Coalesce,

            /// <summary>primary = "(" or ")" | empty( or ) | any( or , or ) | union( or { , or } ) | literal | placeholder | path</summary>
            Primary,
        }

        /// <summary>Where a rule stands: what it asked for last, which says what it does with what that rule read.</summary>
        private enum Stage
        {
            /// <summary>Not begun.</summary>
            Begun,

            /// <summary>Its next term asked for: an <c>or</c>'s, an <c>and</c>'s, a <c>??</c>'s or a <c>union</c>'s.</summary>
            Term,

            /// <summary>A comparison's right side asked for.</summary>
            Right,

            /// <summary>A group's inside asked for.</summary>
            Group,

            /// <summary>An <c>empty(...)</c>'s inside asked for.</summary>
            Empty,

            /// <summary>An <c>any(...)</c>'s list asked for.</summary>
            List,

            /// <summary>An <c>any(...)</c>'s test asked for.</summary>
            Test,

            /// <summary>A <c>union(...)</c>'s next list asked for.</summary>
            Union,
        }

        /// <summary>One rule being read: where it stands, and what it has read so far.</summary>
        private sealed class Rule(Kind kind)
        {
            public Kind Kind { get; } = kind;

            public Stage Stage { get; set; }

            /// <summary>The terms an <c>or</c>, an <c>and</c>, a <c>??</c> or a <c>union</c> has read.</summary>
            public List<FlowExpression> Terms { get; } = [];

            /// <summary>A comparison's left side, or an <c>any(...)</c>'s list.</summary>
            public FlowExpression? Left { get; set; }

            /// <summary>A comparison's operator.</summary>
            public string? Operator { get; set; }

            /// <summary>How many <c>!</c> a <c>not</c> read before what they apply to.</summary>
            public int Nots { get; set; }
        }

        /// <summary>The one expression the text holds from where the reader sits, read as far as the grammar reaches.</summary>
        public FlowExpression Expression()
        {
            var rules = new Stack<Rule>();
            FlowExpression? read = null;

            rules.Push(new Rule(Kind.Or));

            while (rules.TryPeek(out var rule))
            {
                if (Step(rule, ref read) is { } asked)
                {
                    rules.Push(asked);
                    read = null;
                    continue;
                }

                // The rule ended: what it read is what the rule under it asked for.
                _ = rules.Pop();
            }

            return read!;
        }

        /// <summary>
        /// Moves <paramref name="rule"/> on, given <paramref name="read"/> - what the rule it asked for last read: the rule
        /// it asks for next, or null where it ended, what it read left in <paramref name="read"/>.
        /// </summary>
        private Rule? Step(Rule rule, ref FlowExpression? read)
        {
            switch (rule.Kind)
            {
                case Kind.Or:
                    return Joined(rule, ref read, "||", Kind.And, terms => new Or(terms));

                case Kind.And:
                    return Joined(rule, ref read, "&&", Kind.Cmp, terms => new And(terms));

                case Kind.Coalesce:
                    return Joined(rule, ref read, "??", Kind.Primary, terms => new Coalesce(terms));

                case Kind.Cmp:
                    switch (rule.Stage)
                    {
                        case Stage.Begun:
                            rule.Stage = Stage.Term;
                            return new Rule(Kind.Not);

                        case Stage.Term:
                            rule.Left = read;
                            rule.Operator = Takes("==") ? "==" : Takes("!=") ? "!=" : null;

                            if (rule.Operator is null)
                            {
                                return null;
                            }

                            rule.Stage = Stage.Right;
                            return new Rule(Kind.Not);

                        default:
                            read = new Compare(rule.Left!, rule.Operator!, read!);
                            return null;
                    }

                case Kind.Not:
                    if (rule.Stage == Stage.Begun)
                    {
                        while (Takes("!"))
                        {
                            rule.Nots++;
                        }

                        rule.Stage = Stage.Term;
                        return new Rule(Kind.Coalesce);
                    }

                    for (var not = 0; not < rule.Nots; not++)
                    {
                        read = new Not(read!);
                    }

                    return null;

                default:
                    return Primary(rule, ref read);
            }
        }

        /// <summary>
        /// One rule that joins terms of <paramref name="term"/> by <paramref name="operator"/>: its first term asked for,
        /// then one more for each operator the text has next; one term is itself, more are joined by <paramref name="join"/>.
        /// </summary>
        private Rule? Joined(Rule rule, ref FlowExpression? read, string @operator, Kind term, Func<List<FlowExpression>, FlowExpression> join)
        {
            if (rule.Stage == Stage.Begun)
            {
                rule.Stage = Stage.Term;
                return new Rule(term);
            }

            rule.Terms.Add(read!);

            if (Takes(@operator))
            {
                return new Rule(term);
            }

            read = rule.Terms.Count == 1 ? rule.Terms[0] : join(rule.Terms);
            return null;
        }

        /// <summary>primary = "(" or ")" | empty( or ) | any( or , or ) | union( or { , or } ) | literal | placeholder | path</summary>
        private Rule? Primary(Rule rule, ref FlowExpression? read)
        {
            switch (rule.Stage)
            {
                case Stage.Group:
                    Expect(')');
                    return null;

                case Stage.Empty:
                    Expect(')');
                    read = new Empty(read!);
                    return null;

                case Stage.List:
                    rule.Left = read;
                    Expect(',');
                    rule.Stage = Stage.Test;
                    return new Rule(Kind.Or);

                case Stage.Test:
                    Expect(')');
                    read = new Any(rule.Left!, read!);
                    return null;

                case Stage.Union:
                    rule.Terms.Add(read!);

                    if (Takes(","))
                    {
                        return new Rule(Kind.Or);
                    }

                    Expect(')');
                    read = new Union(rule.Terms);
                    return null;
            }

            var at = Skip();

            if (at >= _text.Length)
            {
                throw new ArgumentException($"the expression ends at column {at + 1} where a value was to be read", Text);
            }

            if (_text[at] == '(')
            {
                At = at + 1;
                rule.Stage = Stage.Group;
                return new Rule(Kind.Or);
            }

            if (_text[at] == '{')
            {
                var end = _text.IndexOf('}', at + 1);

                if (end < 0)
                {
                    throw new ArgumentException($"the placeholder at column {at + 1} is never closed", Text);
                }

                At = end + 1;
                read = new Placeholder(_text[(at + 1)..end].Trim());
                return null;
            }

            if (_text[at] is '\'' or '"')
            {
                read = String(at);
                return null;
            }

            if (char.IsDigit(_text[at]))
            {
                read = Number(at);
                return null;
            }

            if (!StartsName(_text[at]))
            {
                throw new ArgumentException($"column {at + 1}: '{_text[at]}' does not start a value", Text);
            }

            var (name, next) = Name(at);
            At = next;

            switch (name)
            {
                case "empty":
                    Expect('(');
                    rule.Stage = Stage.Empty;
                    return new Rule(Kind.Or);

                case "any":
                    Expect('(');
                    rule.Stage = Stage.List;
                    return new Rule(Kind.Or);

                case "union":
                    Expect('(');
                    rule.Stage = Stage.Union;
                    return new Rule(Kind.Or);

                case "true" or "false":
                    read = new Literal(name == "true");
                    return null;

                case "null":
                    read = new Literal(null);
                    return null;
            }

            var walked = Walk(name);

            if (string.Equals(name, "previous", StringComparison.Ordinal) && walked.Steps.Count == 0)
            {
                throw new ArgumentException($"column {at + 1}: 'previous' names a step's work; say which one", Text);
            }

            read = walked;
            return null;
        }

        /// <summary>
        /// path = root { "." key | "[" int "]" }, the root already read: written whole, so a dot or a bracket is part
        /// of the path only where it follows the name with no space between.
        /// </summary>
        private Path Walk(string root)
        {
            var steps = new List<Segment>();

            while (true)
            {
                var at = At;

                if (at < _text.Length && _text[at] == '.' && at + 1 < _text.Length && StartsName(_text[at + 1]))
                {
                    var (key, next) = Name(at + 1);
                    steps.Add(new Segment(key, null));
                    At = next;
                }
                else if (at < _text.Length && _text[at] == '[')
                {
                    var close = _text.IndexOf(']', at + 1);

                    if (close < 0 || WorkflowExpressions.WholeNumber(_text[(at + 1)..close]) is not { } index)
                    {
                        throw new ArgumentException($"the index at column {at + 1} is not a whole number from 0, written in digits alone with no leading zero", Text);
                    }

                    steps.Add(new Segment(null, index));
                    At = close + 1;
                }
                else
                {
                    return new Path(root, steps);
                }
            }
        }

        /// <summary>A quoted string literal, its quote at <paramref name="at"/>.</summary>
        private Literal String(int at)
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
        private Literal Number(int at)
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

            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var fraction)
                ? new Literal(fraction)
                : throw new ArgumentException($"column {at + 1}: '{text}' is not a number an expression reads", Text);
        }

        /// <summary>Reads <paramref name="operator"/> where the text has it next, moving past it.</summary>
        private bool Takes(string @operator)
        {
            var at = Skip();

            if (at + @operator.Length > _text.Length || string.CompareOrdinal(_text, at, @operator, 0, @operator.Length) != 0)
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
