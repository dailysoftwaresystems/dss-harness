using System.Text.RegularExpressions;
using DssHarness.Core.Text;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// What one element of markup says, read alike in every markup a file holds - an HTML page, a Vue component's template,
/// a Razor file's markup - with each kind's own syntax a table over it. Every kind says the same of a class and an id:
/// each class a <c>uses</c> of the rule <c>.name</c>, split as HTML splits a class list, at ASCII white space, and an id
/// a <c>uses</c> of <c>#id</c>, both looked for among rules alone (<see cref="CodeQualifierKinds.Selector"/>). Each kind
/// says which tag is a component's - an <c>instantiates</c> of it - and what each attribute's value is to it
/// (<see cref="MarkupRole"/>): a handler's code, a bound expression, a loop, the names a slot declares, or nothing the
/// graph reads. What that code calls, uses and declares is never the dialect's to say but its language's reader's: each
/// markup's reader gives it the code as its framework compiles the value (<see cref="EmbeddedCode.Script"/>).
/// </summary>
internal sealed class MarkupDialect
{
    private readonly Func<MarkupElement, (string Name, ExtractQualifier? Qualifier)?> _component;
    private readonly Func<MarkupAttribute, MarkupRole> _role;
    private readonly Func<string, (string Aliases, string List, int ListAt)?>? _loop;
    private readonly Func<MarkupText, IEnumerable<MarkupBlock>>? _blocks;

    private MarkupDialect(
        bool closesAsHtml,
        Func<MarkupElement, (string Name, ExtractQualifier? Qualifier)?> component,
        Func<MarkupAttribute, MarkupRole> role,
        Func<string, (string Aliases, string List, int ListAt)?>? loop = null,
        Func<MarkupText, IEnumerable<MarkupBlock>>? blocks = null)
    {
        ClosesAsHtml = closesAsHtml;
        _component = component;
        _role = role;
        _loop = loop;
        _blocks = blocks;
    }

    /// <summary>
    /// An HTML page's: a custom element - a tag with a dash, <c>&lt;my-widget&gt;</c> - an instantiates of a component of
    /// that name; an <c>on*</c> attribute a handler, the body of the function HTML makes of it; its elements closing as
    /// HTML closes them.
    /// </summary>
    public static MarkupDialect Html { get; } = new(
        closesAsHtml: true,
        element => element.Name.Contains('-', StringComparison.Ordinal) ? (element.Name, null) : null,
        attribute => attribute.Name.Length > 2 && attribute.Name.StartsWith("on", StringComparison.Ordinal) && !attribute.Name.Contains('-', StringComparison.Ordinal) ? MarkupRole.Handler : MarkupRole.None);

    /// <summary>
    /// A Vue template's: a component's tag - PascalCase as written, or with a dash, which names the component its
    /// PascalCase form does, as Vue resolves <c>&lt;foo-bar&gt;</c> and <c>&lt;FooBar&gt;</c> alike - an instantiates;
    /// a <c>v-on:x</c> or <c>@x</c> attribute, its modifiers among them (<c>@click.prevent</c>), a handler; a
    /// <c>v-bind:x</c>, <c>:x</c> or <c>.x</c> attribute a binding, as is every other directive whose value is an
    /// expression - <c>v-model</c>, <c>v-if</c>, <c>v-show</c>, a <c>v-bind</c> or <c>v-on</c> of an object, a directive
    /// of the component's own; a <c>v-for</c> a loop over the list its expression names, its aliases names of its own; a
    /// <c>v-slot</c> or <c>#x</c> one that declares the names its value writes; its tags matched as written.
    /// </summary>
    public static MarkupDialect Vue { get; } = new(closesAsHtml: false, element => Named(element.Tag) ?? (element.Name.Contains('-', StringComparison.Ordinal) ? (Pascal(element.Name), null) : null), VueRole, VueLoop);

    /// <summary>
    /// A Razor file's: a PascalCase or dotted tag an instantiates of the component it names, and a
    /// <c>&lt;partial name=&gt;</c> one of the view it names; an <c>@on*</c> attribute a handler, as are <c>@bind:after</c>
    /// and <c>@bind:set</c>; an <c>@bind</c>, <c>@bind-Value</c> or <c>@bind:get</c> a binding, as is any other directive
    /// attribute - <c>@key</c>, <c>@ref</c> - but <c>@formname</c>; a directive attribute's other modifiers - its
    /// <c>:event</c>, <c>:format</c>, <c>:preventDefault</c> - nothing; its tags matched as written.
    /// </summary>
    public static MarkupDialect Razor { get; } = new(
        closesAsHtml: false,
        element => element.Name == "partial" ? element.Attribute("name") is { Length: > 0 } partial ? (ExtractText.Whole(partial), null) : null : Named(element.Tag),
        attribute => RazorRole(attribute));

    /// <summary>Whether its elements close as HTML closes a page's, where their end tags are left out; else as written.</summary>
    public bool ClosesAsHtml { get; }

    /// <summary>The qualifier of a selector: the name, written as a rule writes it, is looked for among rules alone.</summary>
    public static ExtractQualifier Selector { get; } = new(CodeQualifierKinds.Selector);

    /// <summary>
    /// An Angular template's: a component's selector - a tag with a dash, <c>&lt;app-cart&gt;</c>, the component whose
    /// <c>selector</c> it is - an instantiates; a <c>(x)</c> or <c>on-x</c> attribute a handler; a <c>[x]</c>, <c>bind-x</c> or
    /// <c>[(x)]</c> attribute a binding, as is a structural directive's - <c>*ngIf</c>, <c>ngIf</c> - and a <c>*ngFor</c> a loop
    /// over the list its expression names, its <c>let</c> and <c>as</c> names its own; a <c>#x</c> or <c>ref-x</c> attribute a name
    /// declared for the element and what it holds; and a control-flow block its text writes - <c>@if</c>, <c>@for</c>,
    /// <c>@switch</c> - its expression a binding, a <c>@for</c>'s aliases its block's; its elements closing as HTML closes them.
    /// </summary>
    public static MarkupDialect Angular { get; } = new(
        closesAsHtml: true,
        element => element.Name.Contains('-', StringComparison.Ordinal) ? (element.Name, null) : null,
        AngularRole,
        AngularLoop,
        AngularBlocks);

    /// <summary>
    /// A loop's value as this kind of markup reads it: its aliases and the expression of the list it walks, with where that
    /// expression begins in the value; <see langword="null"/> where the value is no loop, or the kind's markup writes none.
    /// </summary>
    public (string Aliases, string List, int ListAt)? Loop(string value) => _loop?.Invoke(value);

    /// <summary>
    /// The control-flow blocks this kind's markup writes in the texts of <paramref name="markup"/>, in the order they are
    /// written - none where the kind writes none (<see cref="MarkupBlock"/>).
    /// </summary>
    public IEnumerable<MarkupBlock> Blocks(MarkupText markup) => _blocks?.Invoke(markup) ?? [];

    /// <summary>
    /// What <paramref name="element"/> itself says, each a reference made from <paramref name="from"/>: the component its
    /// tag makes, and its classes and its id uses of the rules that style them. What its attributes' code says is its
    /// language's reader's (<see cref="Role"/>).
    /// </summary>
    /// <param name="element">The element.</param>
    /// <param name="from">The id of the node the references are made from.</param>
    /// <param name="references">Where the references go.</param>
    public void Read(MarkupElement element, QualifiedName from, List<ExtractReference> references)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(references);

        if (_component(element) is var (component, qualifier) && component.Length > 0)
        {
            references.Add(new ExtractReference(from, CodeEdgeKinds.Instantiates, ExtractText.Whole(component), element.Line) { Qualifier = qualifier });
        }

        foreach (var attribute in element.Attributes)
        {
            switch (attribute.Name)
            {
                case "class":
                    foreach (var name in Classes(attribute.Value))
                    {
                        references.Add(new ExtractReference(from, CodeEdgeKinds.Uses, $".{name}", attribute.Line) { Qualifier = Selector });
                    }

                    break;

                case "id" when Plain(attribute.Value) is { } id:
                    references.Add(new ExtractReference(from, CodeEdgeKinds.Uses, $"#{id}", attribute.Line) { Qualifier = Selector });
                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>What <paramref name="attribute"/>'s value is to this kind of markup.</summary>
    public MarkupRole Role(MarkupAttribute attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        return _role(attribute);
    }

    /// <summary>Whether <paramref name="element"/>'s tag names a component, an <c>instantiates</c> of it, as this kind's syntax says.</summary>
    public bool Instantiated(MarkupElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return _component(element) is var (component, _) && component.Length > 0;
    }

    /// <summary>
    /// A loop's value as Vue reads a <c>v-for</c>'s: its aliases - the parentheses around them taken off, as Vue's compiler
    /// takes them off - and the expression of the list it walks, with where that expression begins in the value;
    /// <see langword="null"/> where the value is no loop.
    /// </summary>
    private static (string Aliases, string List, int ListAt)? VueLoop(string value)
    {
        if (Looped.Match(value) is not { Success: true } loop)
        {
            return null;
        }

        var aliases = loop.Groups["alias"].Value.Trim();

        if (aliases.Length > 1 && aliases[0] == '(' && aliases[^1] == ')')
        {
            aliases = aliases[1..^1];
        }

        return (aliases, loop.Groups["list"].Value, loop.Groups["list"].Index);
    }

    /// <summary>The classes a class attribute holds, split at ASCII white space as HTML's class list is; a name the markup's own expressions write - an <c>@x</c>, a <c>{{ }}</c> - none.</summary>
    internal static IEnumerable<string> Classes(string value)
        => value.Split([' ', '\t', '\n', '\f', '\r'], StringSplitOptions.RemoveEmptyEntries).Select(Plain).OfType<string>();

    /// <summary><paramref name="written"/> as the name of a rule's selector: whole text, and none where an expression of the markup writes it.</summary>
    internal static string? Plain(string written)
    {
        var name = written.Trim();
        return name.Length == 0 || name.AsSpan().IndexOfAny('{', '}', '@') >= 0 ? null : ExtractText.Whole(name);
    }

    /// <summary>A tag a component is named by as written: a PascalCase one, or one dotted, the component its last part names in what the parts before it name.</summary>
    private static (string Name, ExtractQualifier? Qualifier)? Named(string tag)
    {
        if (tag.Length == 0)
        {
            return null;
        }

        var dot = tag.LastIndexOf('.');

        if (dot > 0 && dot < tag.Length - 1)
        {
            return (tag[(dot + 1)..], new ExtractQualifier(CodeQualifierKinds.Named, tag[..dot]));
        }

        return char.IsUpper(tag[0]) && dot < 0 ? (tag, null) : null;
    }

    /// <summary>A dashed name's PascalCase form, as Vue reads a component's: <c>child-item</c> is <c>ChildItem</c>, and <c>ChildItem</c> itself.</summary>
    internal static string Pascal(string tag)
        => string.Concat(tag.Split('-', StringSplitOptions.RemoveEmptyEntries).Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

    /// <summary>
    /// A loop's value as Angular reads it - a <c>*ngFor</c>'s, <c>let item of items; index as i; trackBy: name</c>, or a
    /// <c>@for</c>'s, <c>item of items; track item.id</c>: its aliases - each name after a <c>let</c> and each a binding's <c>as</c>
    /// names, and the names before the <c>of</c> where no <c>let</c> writes them - apart by commas, and the expression of the list
    /// it walks, with where that expression begins in the value; <see langword="null"/> where the value is no loop.
    /// </summary>
    private static (string Aliases, string List, int ListAt)? AngularLoop(string value)
    {
        if (AngularList.Match(value) is not { Success: true } loop)
        {
            return null;
        }

        var list = loop.Groups["list"];
        var leading = list.Value.Length - list.Value.TrimStart().Length;
        var bound = loop.Groups["alias"].Value.Trim();
        var names = AngularAlias.Matches(value).Select(match => match.Groups["name"].Value).ToList();

        // A @for binds its aliases by their names alone, before of; a *ngFor writes let before each.
        if (!bound.StartsWith("let", StringComparison.Ordinal) && bound.Length > 0 && bound.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '$' or ','))
        {
            names.AddRange(bound.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        return (string.Join(", ", names), list.Value.Trim(), list.Index + leading);
    }

    /// <summary>The list an Angular loop walks: what follows its <c>of</c>, to the <c>;</c> its next binding stands behind; before it, its aliases - a let's or a @for's own.</summary>
    private static readonly Regex AngularList = LinearRegex.Compile(@"^\s*(?<alias>[\s\S]*?)\s+of\s+(?<list>[^;]+)");

    /// <summary>A name a <c>*ngFor</c> binds: each after a <c>let</c>, and each a binding's <c>as</c> names - <c>index as i</c>.</summary>
    private static readonly Regex AngularAlias = LinearRegex.Compile(@"\b(?:let|as)\s+(?<name>[A-Za-z_$][\w$]*)");

    /// <summary>
    /// What an Angular attribute is, by its name as HTML names it, in lower case so a directive's is read alike however it is
    /// cased: an event's - <c>(click)</c>, <c>on-click</c>; a binding's - <c>[x]</c>, <c>bind-x</c>, <c>[(x)]</c>, and another
    /// structural directive's, <c>*ngIf</c> and <c>ngIf</c> among them; a loop's - <c>*ngFor</c>, <c>ngFor</c>; a name declared for
    /// what the element holds - <c>#x</c>, <c>ref-x</c>; HTML's own, nothing. HTML's <c>on*=</c> is no Angular mark: a page's own.
    /// </summary>
    private static MarkupRole AngularRole(MarkupAttribute attribute)
    {
        var name = attribute.Name;

        if (name.Length > 2 && (name[0] == '(' && name[^1] == ')' || name.StartsWith("on-", StringComparison.Ordinal)))
        {
            return MarkupRole.Handler;
        }

        if (name.Length > 2 && (name[0] == '[' && name[^1] == ']' || name.StartsWith("bind-", StringComparison.Ordinal)))
        {
            return MarkupRole.Binding;
        }

        if (name is "*ngfor" or "ngfor")
        {
            return MarkupRole.Loop;
        }

        if ((name.Length > 1 && name[0] == '*') || name is "ngif" or "ngswitch" or "ngtemplate")
        {
            return MarkupRole.Binding;
        }

        return name.Length > 1 && (name[0] == '#' || name.StartsWith("ref-", StringComparison.Ordinal)) ? MarkupRole.Declares : MarkupRole.None;
    }

    /// <summary>The words a control-flow block Angular's template writes begins with.</summary>
    private static readonly HashSet<string> BlocksOf = new(StringComparer.Ordinal) { "if", "else", "for", "switch", "case", "empty", "default" };

    /// <summary>
    /// The control-flow blocks Angular's template writes in the texts of <paramref name="markup"/> - each <c>@if</c>, an
    /// <c>@else if</c> read as one, <c>@for</c>, <c>@switch</c>, <c>@case</c>, <c>@empty</c>, <c>@default</c> and <c>@else</c> - in the order
    /// written: the expression its parentheses hold and where it begins, and the stretch its braces hold, each block ending where
    /// its braces close whatever blocks it holds. The braces are matched over the texts alone - an element's attribute values never
    /// counted, for their braces are balanced within the element - and a <c>{{ }}</c>'s and an escaped <c>@{</c>'s are no block's.
    /// </summary>
    private static IEnumerable<MarkupBlock> AngularBlocks(MarkupText markup)
    {
        var text = markup.Text;
        var blocks = new List<OpenBlock>();
        var open = new Stack<OpenBlock>();
        var depth = 0;

        foreach (var (runStart, runEnd) in markup.Texts)
        {
            for (var at = runStart; at < runEnd; at++)
            {
                var character = text[at];

                if (character == '@')
                {
                    // An escaped brace, @{ or @}: no block's, and no brace of a body's.
                    if (at + 1 < runEnd && text[at + 1] is '{' or '}')
                    {
                        at++;
                        continue;
                    }

                    if (Header(text, at, runEnd) is not { } header || !BlocksOf.Contains(header.Kind))
                    {
                        continue;
                    }

                    var block = new OpenBlock(at, header.Kind == "else if" ? "if" : header.Kind, header.Expression, header.ExpressionAt, header.Body);
                    blocks.Add(block);

                    if (header.Body < 0)
                    {
                        at = header.Past - 1;
                        continue;
                    }

                    block.Depth = ++depth;
                    open.Push(block);
                    at = header.Body;
                    continue;
                }

                if (character == '{')
                {
                    if (at + 1 < runEnd && text[at + 1] == '{')
                    {
                        var close = text.Find("}}", at + 2, runEnd);
                        at = close < 0 ? runEnd - 1 : close + 1;
                        continue;
                    }

                    depth++;
                    continue;
                }

                if (character == '}' && depth > 0)
                {
                    if (open.Count > 0 && open.Peek().Depth == depth)
                    {
                        open.Pop().End = at;
                    }

                    depth--;
                }
            }
        }

        return [.. blocks.Select(block => new MarkupBlock(block.At, block.Kind, block.Expression, block.ExpressionAt, block.Body < 0 ? -1 : block.Body + 1, block.End))];
    }

    /// <summary>A block's header at the <c>@</c> at <paramref name="at"/>: its word, its expression where it holds one, and the brace its body begins at; <see langword="null"/> where no word follows.</summary>
    private static (string Kind, string? Expression, long ExpressionAt, long Body, long Past)? Header(CodeText text, long at, long end)
    {
        if (at + 1 >= end || !char.IsAsciiLetter(text[at + 1]))
        {
            return null;
        }

        var word = at + 1;

        for (; word < end && char.IsAsciiLetter(text[word]); word++)
        {
        }

        var kind = text.Slice(at + 1, word);
        var rest = word;

        for (; rest < end && char.IsWhiteSpace(text[rest]); rest++)
        {
        }

        if (kind == "else" && text.At(rest, "if") && (rest + 2 >= end || !char.IsAsciiLetter(text[rest + 2])))
        {
            kind = "else if";

            for (rest += 2; rest < end && char.IsWhiteSpace(text[rest]); rest++)
            {
            }
        }

        string? expression = null;
        var expressionAt = -1L;

        if (rest < end && text[rest] == '(')
        {
            var parentheses = 0;
            var close = rest;

            for (; close < end; close++)
            {
                if (text[close] == '(')
                {
                    parentheses++;
                }
                else if (text[close] == ')' && --parentheses == 0)
                {
                    break;
                }
            }

            (expression, expressionAt) = (text.Slice(rest + 1, Math.Min(close, end)), rest + 1);

            for (rest = close < end ? close + 1 : rest; rest < end && char.IsWhiteSpace(text[rest]); rest++)
            {
            }
        }

        var body = rest < end && text[rest] == '{' ? rest : -1L;

        return (kind, expression, expressionAt, body, body < 0 ? rest : body + 1);
    }

    /// <summary>A block whose braces are being matched: where its body ends once its closing brace is read.</summary>
    private sealed class OpenBlock(long at, string kind, string? expression, long expressionAt, long body)
    {
        public long At { get; } = at;

        public string Kind { get; } = kind;

        public string? Expression { get; } = expression;

        public long ExpressionAt { get; } = expressionAt;

        public long Body { get; } = body;

        /// <summary>How many braces are open once its own is: what tells its closing brace from one a block it holds closes.</summary>
        public int Depth { get; set; }

        /// <summary>Where its closing brace stands; -1 where none was read.</summary>
        public long End { get; set; } = -1;
    }

    /// <summary>What a Vue attribute is: a directive by its name, its argument and modifiers past it (<c>v-model:title.trim</c> a <c>v-model</c>).</summary>
    private static MarkupRole VueRole(MarkupAttribute attribute)
    {
        var name = attribute.Name;

        if (name.StartsWith('@') || name.StartsWith("v-on:", StringComparison.Ordinal))
        {
            return MarkupRole.Handler;
        }

        if (name.StartsWith(':') || name.StartsWith('.'))
        {
            return MarkupRole.Binding;
        }

        if (name.StartsWith('#'))
        {
            return MarkupRole.Declares;
        }

        if (!name.StartsWith("v-", StringComparison.Ordinal))
        {
            return MarkupRole.None;
        }

        return (name.IndexOfAny([':', '.']) is var end and > 0 ? name[..end] : name) switch
        {
            "v-for" => MarkupRole.Loop,
            "v-slot" => MarkupRole.Declares,
            _ => MarkupRole.Binding,
        };
    }

    /// <summary>
    /// What a Razor attribute is: a directive attribute by its name and modifier - <c>@formname</c>, whose value is a
    /// name, nothing; another nothing of its own, the expressions its value writes being Razor's to find, as in a text.
    /// </summary>
    private static MarkupRole RazorRole(MarkupAttribute attribute)
    {
        var name = attribute.Name;

        if (!name.StartsWith('@') || name == "@formname")
        {
            return MarkupRole.None;
        }

        var colon = name.IndexOf(':', StringComparison.Ordinal);
        var directive = colon < 0 ? name : name[..colon];
        var modifier = colon < 0 ? null : name[(colon + 1)..];

        return directive.StartsWith("@on", StringComparison.Ordinal) ? modifier is null ? MarkupRole.Handler : MarkupRole.None
            : directive == "@bind" || directive.StartsWith("@bind-", StringComparison.Ordinal) ? modifier switch
            {
                null or "get" => MarkupRole.Binding,
                "after" or "set" => MarkupRole.Handler,
                _ => MarkupRole.None,
            }
            : modifier is null ? MarkupRole.Binding : MarkupRole.None;
    }

    /// <summary>A loop's value as Vue's compiler reads it: its aliases, <c>in</c> or <c>of</c>, and the expression of the list it walks.</summary>
    private static readonly Regex Looped = LinearRegex.Compile(@"^\s*(?<alias>[\s\S]*?)\s+(?:in|of)\s+(?<list>\S[\s\S]*)$");
}

/// <summary>What an attribute's value is to the markup that writes it.</summary>
internal enum MarkupRole
{
    /// <summary>Nothing the graph reads.</summary>
    None,

    /// <summary>A handler: code the event runs, as its framework compiles it.</summary>
    Handler,

    /// <summary>A binding: an expression whose value the element is given.</summary>
    Binding,

    /// <summary>A loop: the expression of the list it walks, its aliases declared for what the element holds.</summary>
    Loop,

    /// <summary>A slot's: the names its value writes, declared for what the element holds.</summary>
    Declares,
}

/// <summary>
/// One control-flow block an Angular template writes in its text - <c>@if</c>, an <c>@else if</c> read as an <c>if</c>, <c>@for</c>,
/// <c>@switch</c>, <c>@case</c>, <c>@empty</c>, <c>@default</c> or <c>@else</c> - with the expression its parentheses hold and where it
/// begins, and the stretch its braces hold: from past its opening brace to its closing one; -1 where its braces are not read.
/// </summary>
/// <param name="At">Where its <c>@</c> stands.</param>
/// <param name="Kind">Its word: <c>if</c>, <c>else</c>, <c>for</c>, <c>switch</c>, <c>case</c>, <c>empty</c> or <c>default</c>.</param>
/// <param name="Expression">The expression its parentheses hold; <see langword="null"/> where it holds none.</param>
/// <param name="ExpressionAt">Where its expression begins; -1 where it holds none.</param>
/// <param name="Body">Where its body begins, past its opening brace; -1 where its brace is not read.</param>
/// <param name="End">Where its body ends, at its closing brace; -1 where it is not read.</param>
internal sealed record MarkupBlock(long At, string Kind, string? Expression, long ExpressionAt, long Body, long End);
