using System.Text.RegularExpressions;
using DssHarness.Core.Text;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// What a framework's template says, read alike for a Vue component's template block and an Angular template file: each
/// element by the markup's one rule (<see cref="MarkupDialect.Read"/> - its tag, its classes and its id), and the code its
/// attributes and its texts hold given the JavaScript reader as the framework's compiler writes it
/// (<see cref="EmbeddedCode.Script"/>): a handler the body of a function the event runs, a binding and a loop's list their
/// expressions, a loop's aliases and a name the template declares bound where they stand, each <c>{{ }}</c> what its
/// expression reads, and, where the dialect writes them, each control-flow block's expression and a <c>@for</c>'s aliases
/// over its block. Each piece is the body of a function of its own whose parameters are the names the template declares
/// where it stands that it names (<see cref="DeclaredNames"/>); each name a piece reads by itself is a member of the node
/// its code is the code of - the component - but the framework's globals (<see cref="Members"/>).
/// </summary>
internal static class MarkupTemplates
{
    /// <summary>The globals a framework's template compiler leaves to themselves, as Vue's own list has them: no member of the component.</summary>
    private static readonly HashSet<string> Globals = new(StringComparer.Ordinal)
    {
        "Infinity", "undefined", "NaN", "isFinite", "isNaN", "parseFloat", "parseInt", "decodeURI", "decodeURIComponent", "encodeURI",
        "encodeURIComponent", "Math", "Number", "Date", "Array", "Object", "Boolean", "String", "RegExp", "Map", "Set", "JSON", "Intl", "BigInt",
        "console", "Error", "Symbol",
    };

    /// <summary>
    /// The code the template holds, in one walk over its elements and its texts in the order they are written - every element
    /// where <paramref name="inside"/> is null, else those written inside it: each element read by the markup's one rule, its
    /// tag and its classes and id references of <paramref name="component"/>'s; and the code its attributes and its texts'
    /// <c>{{ }}</c> and blocks hold, given the JavaScript reader as <paramref name="dialect"/>'s framework writes it
    /// (<see cref="Handler"/>), each piece the body of a function of its own whose parameters are the names the template
    /// declares where it stands that it names (<see cref="DeclaredNames"/>): the aliases of each loop around it - for that
    /// element's own attributes too, its list read outside them - and the names the element declares for what it holds; a
    /// slot's pattern a function's parameters where it is written, so its defaults are code read once. Answers those
    /// functions, each the component's.
    /// </summary>
    /// <param name="markup">The markup read.</param>
    /// <param name="dialect">The kind of markup, whose roles, loops and blocks are read.</param>
    /// <param name="component">The node the template's code is the code of: a Vue component, an Angular template's own node.</param>
    /// <param name="references">Where the elements' references go.</param>
    /// <param name="inside">The element the template is written inside, a Vue template block; null for the whole markup.</param>
    public static List<CodeRegion> Read(MarkupText markup, MarkupDialect dialect, QualifiedName component, List<ExtractReference> references, MarkupElement? inside)
    {
        ArgumentNullException.ThrowIfNull(markup);
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(references);

        var elements = markup.Elements;
        var regions = new List<CodeRegion>();
        var declared = new DeclaredNames();
        var texts = markup.Texts.Where(text => inside is null || text.Start >= inside.OpenEnd && text.End <= inside.Close).ToList();
        var blocks = dialect.Blocks(markup).ToList();
        var nextBlock = 0;
        var nextText = 0;

        for (var at = 0; at <= elements.Count; at++)
        {
            var start = at < elements.Count ? elements[at].Start : long.MaxValue;

            // Each run of text before the element, in the elements around it: its blocks and its {{ }}, in the order they are written.
            for (; nextText < texts.Count && texts[nextText].Start < start; nextText++)
            {
                var (textStart, textEnd) = texts[nextText];
                declared.Leave(textStart);
                var mustaches = Mustaches.Matches(markup.Text.Slice(textStart, textEnd)).Cast<Match>().Select(match => (At: textStart + match.Groups["code"].Index, match.Groups["code"].Value)).ToList();
                var headers = new List<(long At, MarkupBlock Block)>();

                for (; nextBlock < blocks.Count && blocks[nextBlock].At < textEnd; nextBlock++)
                {
                    headers.Add((blocks[nextBlock].At, blocks[nextBlock]));
                }

                var mustache = 0;
                var header = 0;

                while (mustache < mustaches.Count || header < headers.Count)
                {
                    if (header < headers.Count && (mustache >= mustaches.Count || headers[header].At < mustaches[mustache].At))
                    {
                        Block(dialect, declared, headers[header].Block, Add);
                        header++;
                        continue;
                    }

                    if (mustaches[mustache].Value is { Length: > 0 } code && !string.IsNullOrWhiteSpace(code))
                    {
                        Add(mustaches[mustache].At, $"({code});");
                    }

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

            var end = Math.Max(element.Close, element.OpenEnd);
            declared.Leave(element.Start);
            dialect.Read(element, component, references);

            // A loop's list is read in the scope around the element; its aliases are the element's own, its attributes' too.
            var lists = new Dictionary<MarkupAttribute, CodeRegion>();
            var aliases = new List<string>();

            foreach (var attribute in element.Attributes)
            {
                if (dialect.Role(attribute) == MarkupRole.Loop && dialect.Loop(attribute.Value) is var (bound, list, listAt))
                {
                    lists[attribute] = Piece(attribute.ValueAt + listAt, $"({list});");
                    aliases.AddRange(JsReading.Binds(bound));
                }
            }

            declared.Declare(aliases, end);
            var names = new List<string>();

            foreach (var attribute in element.Attributes)
            {
                var role = dialect.Role(attribute);

                // A directive's dynamic argument - :[key], @[event], #[slot] - is an expression of its own, as written.
                if (ReferenceEquals(dialect, MarkupDialect.Vue) && role != MarkupRole.None && Argument(markup.Text, attribute) is var (argumentAt, argument))
                {
                    Add(argumentAt, $"({argument});");
                }

                // A name an element declares for what it holds is written in the attribute's own name - Angular's #ref.
                if (role == MarkupRole.Declares && ReferenceEquals(dialect, MarkupDialect.Angular) && Declared(markup.Text, attribute) is { } named)
                {
                    names.Add(named);
                }

                if (string.IsNullOrWhiteSpace(attribute.Value))
                {
                    continue;
                }

                switch (role)
                {
                    case MarkupRole.Loop when lists.TryGetValue(attribute, out var listed):
                        regions.Add(listed);
                        break;

                    case MarkupRole.Handler:
                        Add(attribute.ValueAt, Handler(attribute.Value));
                        break;

                    case MarkupRole.Binding:
                        Add(attribute.ValueAt, $"({attribute.Value});");
                        break;

                    // A slot's pattern is a function's parameters, read where it is written; the names it binds are what the
                    // element holds.
                    case MarkupRole.Declares when ReferenceEquals(dialect, MarkupDialect.Vue):
                        var given = declared.Given(attribute.Value);
                        regions.Add(new CodeRegion(component, given.Length > 0 ? $"{given}, {attribute.Value}" : attribute.Value, attribute.ValueAt, string.Empty));
                        names.AddRange(JsReading.Binds(attribute.Value));
                        break;

                    default:
                        break;
                }
            }

            declared.Declare(names, end);
        }

        return regions;

        // A piece of code, in a function of the names declared where it stands that it names.
        CodeRegion Piece(long offset, string code) => new(component, declared.Given(code), offset, code);

        // The piece, among the template's regions.
        CodeRegion Add(long offset, string code)
        {
            var region = Piece(offset, code);
            regions.Add(region);
            return region;
        }
    }

    /// <summary>
    /// What one control-flow block's header says (<see cref="MarkupBlock"/>): a <c>@for</c>'s list read in the scope around it and
    /// its aliases declared for its body; any other expression a binding, read where it stands.
    /// </summary>
    private static void Block(MarkupDialect dialect, DeclaredNames declared, MarkupBlock block, Func<long, string, CodeRegion> piece)
    {
        if (block.Expression is null)
        {
            return;
        }

        if (block.Kind == "for" && dialect.Loop(block.Expression) is var (bound, list, listAt))
        {
            piece(block.ExpressionAt + listAt, $"({list});");
            declared.Declare(JsReading.Binds(bound), block.End < 0 ? long.MaxValue : block.End);
            return;
        }

        piece(block.ExpressionAt, $"({block.Expression});");
    }

    /// <summary>
    /// The name an attribute that declares one writes in its own name - <c>#box</c>, <c>ref-box</c> - as the text writes it, in its
    /// case; <see langword="null"/> where it names none.
    /// </summary>
    private static string? Declared(CodeText text, MarkupAttribute attribute)
    {
        var written = text.Substring(attribute.NameAt, Math.Min(attribute.Name.Length, Math.Max(text.Length - attribute.NameAt, 0)));
        var name = written.StartsWith("ref-", StringComparison.Ordinal) ? written[4..] : written.Length > 0 && written[0] == '#' ? written[1..] : string.Empty;

        return name.Length > 0 && (char.IsAsciiLetter(name[0]) || name[0] is '_' or '$') && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '$') ? name : null;
    }

    /// <summary>
    /// The dynamic argument a directive's name holds - <c>v-bind:[key]</c>, <c>@[event]</c>, <c>#[slot]</c> - read where the
    /// text writes it, in its own case, as Vue's compiler reads it: the expression between the brackets after the directive's
    /// name, with where it begins; <see langword="null"/> where the name holds none.
    /// </summary>
    private static (long At, string Code)? Argument(CodeText text, MarkupAttribute attribute)
    {
        var written = text.Substring(attribute.NameAt, Math.Min(attribute.Name.Length, text.Length - attribute.NameAt)).AsSpan();
        var open = written.IndexOf('[');
        var close = written.LastIndexOf(']');

        return open > 0 && close > open + 1 && written[open - 1] is ':' or '@' or '#' or '.' ? (attribute.NameAt + open + 1, written[(open + 1)..close].ToString()) : null;
    }

    /// <summary>
    /// The JavaScript an event attribute's value is compiled to, as Vue's and Angular's compilers write it: a member path -
    /// <c>pay</c>, <c>cart.add</c> - the handler it names, called with what the event gives; a function expression the handler
    /// itself; anything else statements the event runs, its value <c>$event</c>.
    /// </summary>
    public static string Handler(string value)
        => MemberPath.IsMatch(value) ? $"((...args) => {value}(...args));"
            : FunctionExpression.IsMatch(value) ? $"({value});"
            : $"(function ($event) {{ {value} }});";

    /// <summary>
    /// <paramref name="template"/>, what the JavaScript reader made of the template's code, with each name it reads by
    /// itself - no receiver's, no local's, which the reader keeps to itself - a member of the component, as the framework's
    /// compiler reads it from the component's instance; but the globals its compiler leaves to themselves.
    /// </summary>
    public static FileExtract Members(FileExtract template)
        => template with
        {
            References = [.. template.References.Select(reference => reference is { Qualifier: null, Kind: CodeEdgeKinds.Uses or CodeEdgeKinds.Calls } && !Globals.Contains(reference.Name)
                ? reference with { Qualifier = new ExtractQualifier(CodeQualifierKinds.This) }
                : reference)],
        };

    /// <summary>A <c>{{ }}</c> a template's text holds, over lines or on one.</summary>
    private static readonly Regex Mustaches = LinearRegex.Compile(@"\{\{(?<code>.*?)\}\}", RegexOptions.Singleline);

    /// <summary>A member path, as a framework's compiler tells a handler an event attribute names from code it runs: names, each after a dot, an optional chain's <c>?.</c> or between brackets.</summary>
    private static readonly Regex MemberPath = LinearRegex.Compile(@"^\s*[A-Za-z_$][\w$]*(?:\??\.[A-Za-z_$][\w$]*|\['[^']*?'\]|\[""[^""]*?""\]|\[\d+\]|\[[A-Za-z_$][\w$]*\])*\s*$");

    /// <summary>A function expression, as a framework's compiler tells one an event attribute gives: an arrow's head, or a <c>function</c>'s.</summary>
    private static readonly Regex FunctionExpression = LinearRegex.Compile(@"^\s*(?:async\s*)?(?:\([^)]*?\)|[\w$]+)\s*(?::[^=]+)?=>|^\s*(?:async\s+)?function(?:\s+[\w$]+)?\s*\(");
}
