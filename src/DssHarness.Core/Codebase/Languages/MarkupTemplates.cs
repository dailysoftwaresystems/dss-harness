using System.Text.RegularExpressions;
using DssHarness.Core.Text;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// What a framework's template says, read alike for a Vue component's template block and an Angular template file, its
/// dialect's table saying each framework's own syntax and this reader naming none: each element by the markup's one rule
/// (<see cref="MarkupDialect.Read"/> - its tag, its classes and its id), and the code its attributes and its texts hold given
/// the JavaScript reader as the framework's compiler writes it (<see cref="EmbeddedCode.Script"/>): a handler as the dialect
/// compiles an event's value, a binding's, a loop's and a structural directive's expressions, a dynamic argument where the
/// dialect reads one, a <c>{{ }}</c> in a text and, where the dialect interpolates them, in an attribute's value, each with its
/// pipes an instantiates of the pipe by its name (<see cref="MarkupDialect.Expression"/>), and each control-flow block's
/// header. Each piece is the body of a function of its own whose parameters are the names the template declares where it
/// stands that it names (<see cref="DeclaredNames"/>): a loop's aliases over its element, a slot's over what its element
/// holds, a block's over its body, and a name the dialect declares over a whole view - an Angular template reference, a
/// <c>@let</c> - over every piece of that view, wherever it is declared in it. Each name a piece reads by itself is a member of
/// the node its code is the code of - the component - but the globals its framework's compiler leaves alone
/// (<see cref="Members"/>).
/// </summary>
internal static class MarkupTemplates
{
    /// <summary>
    /// The code the template holds, in one walk over its elements and its texts in the order they are written - every element
    /// where <paramref name="inside"/> is null, else those written inside it: each element read by the markup's one rule, its
    /// tag and its classes and id references of the node <paramref name="within"/> gives it; each text's and each attribute's
    /// code, each piece the body of a function of its own whose parameters are the names the template declares where it stands
    /// that it names, the code of the node the innermost element around it is held by - a text's as its element's is - and each
    /// pipe it names an instantiates of the pipe. Answers those functions.
    /// </summary>
    /// <param name="markup">The markup read.</param>
    /// <param name="dialect">The kind of markup, whose table is read.</param>
    /// <param name="component">The node the template's code is the code of where no element around it says another: a Vue component, an Angular template file's own node.</param>
    /// <param name="references">Where the elements' and the pipes' references go.</param>
    /// <param name="inside">The element the template is written inside, a Vue template block; null for the whole markup.</param>
    /// <param name="within">The node each element's references and the code it holds are the code of, where the markup's own sections say - a page's landmarks and ids; null for the component's alone.</param>
    public static List<CodeRegion> Read(MarkupText markup, MarkupDialect dialect, QualifiedName component, List<ExtractReference> references, MarkupElement? inside, Func<MarkupElement, QualifiedName>? within = null)
    {
        ArgumentNullException.ThrowIfNull(markup);
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(references);

        var elements = markup.Elements;
        var regions = new List<CodeRegion>();
        var declared = new DeclaredNames();
        var texts = markup.Texts.Where(text => inside is null || text.Start >= inside.OpenEnd && text.End <= inside.Close).ToList();
        var blocks = dialect.Blocks(markup).ToList();
        var views = TemplateViews.Of(markup, dialect, blocks, inside);

        // The elements open where the walk stands, the innermost on top: what holds a text's code.
        var holding = new Stack<MarkupElement>();
        var nextBlock = 0;
        var nextText = 0;

        // What the template's own view declares is seen from anywhere in it.
        declared.Declare(views.Top, long.MaxValue);

        for (var at = 0; at <= elements.Count; at++)
        {
            var start = at < elements.Count ? elements[at].Start : long.MaxValue;

            // Each run of text before the element, in the elements around it: its blocks and its {{ }}, in the order they are written.
            for (; nextText < texts.Count && texts[nextText].Start < start; nextText++)
            {
                var (textStart, textEnd) = texts[nextText];
                Leave(textStart);
                var holder = holding.Count > 0 ? Holder(holding.Peek()) : component;
                var mustaches = Mustaches.Matches(markup.Text.Slice(textStart, textEnd)).Select(match => (At: textStart + match.Groups["code"].Index, match.Groups["code"].Value)).ToList();
                var headers = new List<MarkupBlock>();

                for (; nextBlock < blocks.Count && blocks[nextBlock].At < textEnd; nextBlock++)
                {
                    headers.Add(blocks[nextBlock]);
                }

                var mustache = 0;
                var header = 0;

                while (mustache < mustaches.Count || header < headers.Count)
                {
                    if (header < headers.Count && (mustache >= mustaches.Count || headers[header].At < mustaches[mustache].At))
                    {
                        declared.Leave(headers[header].At);
                        Block(headers[header], holder);
                        header++;
                        continue;
                    }

                    declared.Leave(mustaches[mustache].At);
                    Code(holder, mustaches[mustache].At, mustaches[mustache].Value);
                    mustache++;
                }
            }

            if (at == elements.Count)
            {
                break;
            }

            var element = elements[at];

            if (inside is not null && !markup.Inside(element, inside))
            {
                continue;
            }

            Leave(element.Start);
            var end = Math.Max(element.Close, element.OpenEnd);
            var holds = Holder(element);
            dialect.Read(element, holds, references);

            // A loop's or a structural directive's code is read in the scope around the element; its names are the element's own,
            // its attributes' too, with what the view it opens declares.
            var aliases = new List<string>();
            var inner = new List<(MarkupAttribute Attribute, MarkupCode Code)>();

            foreach (var attribute in element.Attributes)
            {
                if (dialect.Role(attribute) == MarkupRole.Loop && dialect.Bound(attribute.Value) is { } binding)
                {
                    foreach (var piece in binding.Outer)
                    {
                        Code(holds, attribute.ValueAt + piece.At, piece.Code);
                    }

                    aliases.AddRange(binding.Aliases);
                    inner.AddRange(binding.Inner.Select(piece => (attribute, piece)));
                }
            }

            declared.Declare([.. aliases, .. views.Own(element)], end);

            foreach (var (attribute, piece) in inner)
            {
                Code(holds, attribute.ValueAt + piece.At, piece.Code);
            }

            var names = new List<string>();

            foreach (var attribute in element.Attributes)
            {
                var role = dialect.Role(attribute);

                // A directive's dynamic argument - :[key], @[event], #[slot] - is an expression of its own, as written.
                if (dialect.DynamicArguments && role != MarkupRole.None && Argument(attribute) is var (argumentAt, argument))
                {
                    Add(holds, argumentAt, $"({argument});");
                }

                // A name declared for what the element holds; a slot's pattern a function's parameters, read where it is written.
                if (role == MarkupRole.Declares && dialect.Declaration(attribute) is { View: false } declaration)
                {
                    if (declaration.Pattern is { } pattern)
                    {
                        var given = declared.Given(pattern);
                        regions.Add(new CodeRegion(holds, given.Length > 0 ? $"{given}, {pattern}" : pattern, attribute.ValueAt, string.Empty));
                    }

                    names.AddRange(declaration.Names);
                }

                if (string.IsNullOrWhiteSpace(attribute.Value))
                {
                    continue;
                }

                switch (role)
                {
                    case MarkupRole.Handler:
                        Add(holds, attribute.ValueAt, dialect.Handler(attribute.Value));
                        break;

                    case MarkupRole.Binding:
                        Code(holds, attribute.ValueAt, attribute.Value);
                        break;

                    // An attribute's own {{ }} - title="{{ name }}" - where the dialect reads one as code.
                    case MarkupRole.None when dialect.InterpolatesAttributes:
                        foreach (Match match in Mustaches.Matches(attribute.Value))
                        {
                            Code(holds, attribute.ValueAt + match.Groups["code"].Index, match.Groups["code"].Value);
                        }

                        break;

                    default:
                        break;
                }
            }

            declared.Declare(names, end);

            // What an element that holds a view of its own holds - an ng-template's - declares over itself alone.
            if (dialect.HoldsView(element))
            {
                declared.Declare(views.Content(element), element.Close);
            }

            if (element.Close > element.OpenEnd)
            {
                holding.Push(element);
            }
        }

        return regions;

        // Leaves what ends at or before the place: the names declared over it, and the elements around it.
        void Leave(long offset)
        {
            declared.Leave(offset);

            while (holding.Count > 0 && Math.Max(holding.Peek().Close, holding.Peek().OpenEnd) <= offset)
            {
                holding.Pop();
            }
        }

        QualifiedName Holder(MarkupElement element) => within?.Invoke(element) ?? component;

        // One control-flow block's header: its code read around it, its names and its view's declared over its body, and the code
        // its header reads with them.
        void Block(MarkupBlock block, QualifiedName holder)
        {
            foreach (var piece in block.Binding?.Outer ?? [])
            {
                Code(holder, block.ExpressionAt + piece.At, piece.Code);
            }

            if (block.Body < 0)
            {
                return;
            }

            declared.Declare([.. block.Binding?.Aliases ?? [], .. views.Own(block)], block.End < 0 ? long.MaxValue : block.End);

            foreach (var piece in block.Binding?.Inner ?? [])
            {
                Code(holder, block.ExpressionAt + piece.At, piece.Code);
            }
        }

        // An expression of the markup, the code of <paramref name="node"/>: its pipes each an instantiates of the pipe it names, the
        // code its dialect compiles it to a piece of its own.
        void Code(QualifiedName node, long offset, string written)
        {
            var expression = dialect.Expression(written);

            foreach (var pipe in expression.Pipes)
            {
                references.Add(new ExtractReference(node, CodeEdgeKinds.Instantiates, $"|{pipe.Name}", markup.LineAt(offset + pipe.At)));
            }

            if (!string.IsNullOrWhiteSpace(expression.Code))
            {
                Add(node, offset, $"({expression.Code});");
            }
        }

        // The piece, among the template's regions: the code of <paramref name="node"/>, in a function of the names declared where it stands that it names.
        void Add(QualifiedName node, long offset, string code) => regions.Add(new CodeRegion(node, declared.Given(code), offset, code));
    }

    /// <summary>
    /// The dynamic argument a directive's name holds - <c>v-bind:[key]</c>, <c>@[event]</c>, <c>#[slot]</c> - read where the
    /// text writes it, in its own case: the expression between the brackets after the directive's name, with where it begins;
    /// <see langword="null"/> where the name holds none.
    /// </summary>
    private static (long At, string Code)? Argument(MarkupAttribute attribute)
    {
        var written = attribute.Written.AsSpan();
        var open = written.IndexOf('[');
        var close = written.LastIndexOf(']');

        return open > 0 && close > open + 1 && written[open - 1] is ':' or '@' or '#' or '.' ? (attribute.NameAt + open + 1, written[(open + 1)..close].ToString()) : null;
    }

    /// <summary>
    /// <paramref name="template"/>, what the JavaScript reader made of the template's code, with each name it reads by
    /// itself - no receiver's, no local's, which the reader keeps to itself - a member of the component, as the framework's
    /// compiler reads it from the component's instance; but the globals <paramref name="dialect"/>'s compiler leaves to themselves.
    /// </summary>
    public static FileExtract Members(FileExtract template, MarkupDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(dialect);

        return template with
        {
            References = [.. template.References.Select(reference => reference is { Qualifier: null, Kind: CodeEdgeKinds.Uses or CodeEdgeKinds.Calls } && !dialect.Globals.Contains(reference.Name)
                ? reference with { Qualifier = new ExtractQualifier(CodeQualifierKinds.This) }
                : reference)],
        };
    }

    /// <summary>A <c>{{ }}</c> a template's text or an attribute's value holds, over lines or on one.</summary>
    private static readonly Regex Mustaches = LinearRegex.Compile(@"\{\{(?<code>.*?)\}\}", RegexOptions.Singleline);

    /// <summary>
    /// The names each view of a template declares over the whole of it, wherever it declares them in it - an Angular template
    /// reference (<c>#x</c>), a <c>@let</c> - found in one walk before the template's code is read: the template's own view; an
    /// element a structural directive is written on, itself and what it holds; what an element that holds a view holds (an
    /// <c>ng-template</c>'s content, the element itself standing in the view around it); and each block's body.
    /// </summary>
    private sealed class TemplateViews
    {
        private readonly Dictionary<object, List<string>> _own = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<MarkupElement, List<string>> _content = new(ReferenceEqualityComparer.Instance);

        /// <summary>The names the template's own view declares.</summary>
        public List<string> Top { get; } = [];

        /// <summary>The names the view an element or a block opens declares: none where it opens none.</summary>
        public IReadOnlyList<string> Own(object opened) => _own.TryGetValue(opened, out var names) ? names : [];

        /// <summary>The names the view an element holds declares: none where it holds none.</summary>
        public IReadOnlyList<string> Content(MarkupElement element) => _content.TryGetValue(element, out var names) ? names : [];

        /// <summary>The views of the template <paramref name="markup"/> writes - inside <paramref name="inside"/> where it is given - in its <paramref name="dialect"/>, with its <paramref name="blocks"/>.</summary>
        public static TemplateViews Of(MarkupText markup, MarkupDialect dialect, IReadOnlyList<MarkupBlock> blocks, MarkupElement? inside)
        {
            var views = new TemplateViews();
            var open = new Stack<(long End, List<string> Names)>();
            var elements = markup.Elements;
            var block = 0;

            open.Push((long.MaxValue, views.Top));

            for (var at = 0; at <= elements.Count; at++)
            {
                var start = at < elements.Count ? elements[at].Start : long.MaxValue;

                for (; block < blocks.Count && blocks[block].At < start; block++)
                {
                    var written = blocks[block];
                    Leave(written.At);

                    if (written.Kind == "let")
                    {
                        open.Peek().Names.AddRange(written.Binding?.Aliases ?? []);
                    }
                    else if (written.Body >= 0)
                    {
                        open.Push((written.End < 0 ? long.MaxValue : written.End, views._own[written] = []));
                    }
                }

                if (at == elements.Count)
                {
                    break;
                }

                var element = elements[at];

                if (inside is not null && !markup.Inside(element, inside))
                {
                    continue;
                }

                Leave(element.Start);

                if (dialect.OpensView(element))
                {
                    open.Push((Math.Max(element.Close, element.OpenEnd), views._own[element] = []));
                }

                foreach (var attribute in element.Attributes)
                {
                    if (dialect.Role(attribute) == MarkupRole.Declares && dialect.Declaration(attribute) is { View: true } declaration)
                    {
                        open.Peek().Names.AddRange(declaration.Names);
                    }
                }

                if (dialect.HoldsView(element))
                {
                    open.Push((element.Close, views._content[element] = []));
                }
            }

            return views;

            void Leave(long offset)
            {
                while (open.Count > 1 && open.Peek().End <= offset)
                {
                    open.Pop();
                }
            }
        }
    }
}
