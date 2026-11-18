using System.Text.RegularExpressions;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// What one element of markup says, read alike in every markup a file holds - an HTML page, a Vue component's template,
/// a Razor file's markup - with each kind's own syntax a table over it. Every kind says the same of a class and an id:
/// each class a <c>uses</c> of the rule <c>.name</c>, split as HTML splits a class list, at ASCII white space, and an id
/// a <c>uses</c> of <c>#id</c>, both looked for among rules alone (<see cref="CodeQualifierKinds.Selector"/>). Each kind
/// says which tag is a component's - an <c>instantiates</c> of it - which attributes name a handler - a <c>calls</c> of
/// the function it names, where its value names one alone or one call of it, else a <c>uses</c> of the name it begins
/// with - and which bind a value, a <c>uses</c> of the name its expression begins with. A value is read past an arrow
/// function's head, <c>() =&gt; pay(item)</c> calling <c>pay</c>.
/// </summary>
internal sealed partial class MarkupDialect
{
    /// <summary>The words an expression may begin with that name no member: literals and operators written as words.</summary>
    private static readonly HashSet<string> Words = new(StringComparer.Ordinal)
    {
        "true", "false", "null", "undefined", "this", "new", "typeof", "void", "delete", "await", "function", "in", "of", "instanceof", "default",
    };

    private readonly Func<MarkupElement, (string Name, ExtractQualifier? Qualifier)?> _component;
    private readonly Func<MarkupAttribute, Role> _role;

    private MarkupDialect(bool closesAsHtml, Func<MarkupElement, (string Name, ExtractQualifier? Qualifier)?> component, Func<MarkupAttribute, Role> role)
    {
        ClosesAsHtml = closesAsHtml;
        _component = component;
        _role = role;
    }

    /// <summary>
    /// An HTML page's: a custom element - a tag with a dash, <c>&lt;my-widget&gt;</c> - an instantiates of a component of
    /// that name; an <c>on*</c> attribute a handler; its elements closing as HTML closes them.
    /// </summary>
    public static MarkupDialect Html { get; } = new(
        closesAsHtml: true,
        element => element.Name.Contains('-', StringComparison.Ordinal) ? (element.Name, null) : null,
        attribute => attribute.Name.Length > 2 && attribute.Name.StartsWith("on", StringComparison.Ordinal) && !attribute.Name.Contains('-', StringComparison.Ordinal) ? Role.Handler : Role.None);

    /// <summary>
    /// A Vue template's: a component's tag - PascalCase as written, or with a dash, which names the component its
    /// PascalCase form does, as Vue resolves <c>&lt;foo-bar&gt;</c> and <c>&lt;FooBar&gt;</c> alike - an instantiates;
    /// a <c>v-on:x</c> or <c>@x</c> attribute, its modifiers among them (<c>@click.prevent</c>), a handler; a
    /// <c>v-bind:x</c>, <c>:x</c> or <c>.x</c> attribute a binding, as is every other directive whose value is an
    /// expression - <c>v-model</c>, <c>v-if</c>, <c>v-show</c>, a directive of the component's own; a <c>v-for</c> a
    /// loop over the list its expression names, its aliases names of its own; a <c>v-slot</c> or <c>#x</c> one that
    /// declares the names its value writes; its tags matched as written.
    /// </summary>
    public static MarkupDialect Vue { get; } = new(closesAsHtml: false, element => Named(element.Tag) ?? (element.Name.Contains('-', StringComparison.Ordinal) ? (Pascal(element.Name), null) : null), VueRole);

    /// <summary>
    /// A Razor file's: a PascalCase or dotted tag an instantiates of the component it names, and a
    /// <c>&lt;partial name=&gt;</c> one of the view it names; an <c>@on*</c> attribute a handler, as are <c>@bind:after</c>
    /// and <c>@bind:set</c>; an <c>@bind</c>, <c>@bind-Value</c> or <c>@bind:get</c> a binding, as is any other <c>@</c>
    /// attribute and any attribute whose value is written <c>@name</c>; a directive attribute's other modifiers - its
    /// <c>:event</c>, <c>:format</c>, <c>:preventDefault</c> - nothing; its tags matched as written.
    /// </summary>
    public static MarkupDialect Razor { get; } = new(
        closesAsHtml: false,
        element => element.Name == "partial" ? element.Attribute("name") is { Length: > 0 } partial ? (ExtractText.Whole(partial), null) : null : Named(element.Tag),
        attribute => RazorRole(attribute));

    /// <summary>What an attribute is to a dialect.</summary>
    private enum Role
    {
        /// <summary>Nothing the graph reads.</summary>
        None,

        /// <summary>A handler: a call of the function its value names.</summary>
        Handler,

        /// <summary>A binding: a use of the name its expression begins with.</summary>
        Binding,

        /// <summary>A loop: a use of the name the expression of the list it walks begins with, its aliases declared.</summary>
        Loop,

        /// <summary>A slot's: the names its value writes declared, and nothing used.</summary>
        Declares,
    }

    /// <summary>Whether its elements close as HTML closes a page's, where their end tags are left out; else as written.</summary>
    public bool ClosesAsHtml { get; }

    /// <summary>
    /// What <paramref name="element"/> says, each a reference made from <paramref name="from"/>: its classes and its id
    /// uses of the rules that style them, the component its tag makes, and what each of its handlers calls and its bindings
    /// and loops use - qualified by <paramref name="members"/>, where they are a component's members - but a name
    /// <paramref name="local"/> holds, which the markup declares itself and no member is.
    /// </summary>
    /// <param name="element">The element.</param>
    /// <param name="from">The id of the node the references are made from.</param>
    /// <param name="members">The qualifier of the names its handlers and bindings name: <see cref="CodeQualifierKinds.This"/> where they are the component's members; <see langword="null"/> where they are looked for as any name is.</param>
    /// <param name="references">Where the references go.</param>
    /// <param name="local">The names the markup declares where the element stands - a loop's aliases, a slot's - as <see cref="Declared"/> gives them; none where <see langword="null"/>.</param>
    public void Read(MarkupElement element, string from, ExtractQualifier? members, List<ExtractReference> references, IReadOnlySet<string>? local = null)
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

                    continue;

                case "id" when Plain(attribute.Value) is { } id:
                    references.Add(new ExtractReference(from, CodeEdgeKinds.Uses, $"#{id}", attribute.Line) { Qualifier = Selector });
                    continue;

                default:
                    break;
            }

            (string Kind, string Name)? said = _role(attribute) switch
            {
                Role.Handler when Handled(attribute.Value) is var (name, called) => (called ? CodeEdgeKinds.Calls : CodeEdgeKinds.Uses, name),
                Role.Binding when Begins(attribute.Value) is { } name => (CodeEdgeKinds.Uses, name),
                Role.Loop when Looped().Match(attribute.Value) is { Success: true } loop && Begins(loop.Groups["list"].Value) is { } name => (CodeEdgeKinds.Uses, name),
                _ => null,
            };

            if (said is var (kind, named) && local?.Contains(named) != true)
            {
                references.Add(new ExtractReference(from, kind, named, attribute.Line) { Qualifier = members });
            }
        }
    }

    /// <summary>
    /// The names <paramref name="element"/> declares for itself and what it holds - a <c>v-for</c>'s aliases, the names a
    /// <c>v-slot</c> or <c>#x</c> value writes - each a pattern's name as JavaScript binds it: <c>(item, index)</c> both,
    /// <c>{ row: line }</c> <c>line</c> alone.
    /// </summary>
    public IEnumerable<string> Declared(MarkupElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        foreach (var attribute in element.Attributes)
        {
            var pattern = _role(attribute) switch
            {
                Role.Loop when Looped().Match(attribute.Value) is { Success: true } loop => loop.Groups["alias"].Value,
                Role.Declares => attribute.Value,
                _ => null,
            };

            if (pattern is null)
            {
                continue;
            }

            foreach (Match bound in Bound().Matches(Defaultless().Replace(pattern, string.Empty)))
            {
                yield return bound.Groups["name"].Value;
            }
        }
    }

    /// <summary>The qualifier of a selector: the name, written as a rule writes it, is looked for among rules alone.</summary>
    public static ExtractQualifier Selector { get; } = new(CodeQualifierKinds.Selector);

    /// <summary>
    /// What a handler's value calls - the function it names alone, or the one it calls first, an arrow function's head
    /// passed over - and, where it calls none, the name it begins with, a use; <see langword="null"/> where it begins with
    /// no name.
    /// </summary>
    internal static (string Name, bool Called)? Handled(string value)
    {
        var body = Body(value);

        if (Leading().Match(body) is not { Success: true } leading || Words.Contains(leading.Groups["name"].Value))
        {
            return Begins(value) is { } used ? (used, false) : null;
        }

        var after = body[leading.Length..].TrimStart();

        return after.Length == 0 || after[0] == '(' ? (leading.Groups["name"].Value, true) : Begins(value) is { } named ? (named, false) : null;
    }

    /// <summary>The name an expression begins with - past a <c>!</c>, a parenthesis and an arrow function's head - that is no literal or word of the language; <see langword="null"/> where it begins with none.</summary>
    internal static string? Begins(string value)
        => First().Match(Body(value)) is { Success: true } first && !Words.Contains(first.Groups["name"].Value) ? first.Groups["name"].Value : null;

    /// <summary>The classes a class attribute holds, split at ASCII white space as HTML's class list is; a name the markup's own expressions write - an <c>@x</c>, a <c>{{ }}</c> - none.</summary>
    internal static IEnumerable<string> Classes(string value)
        => value.Split([' ', '\t', '\n', '\f', '\r'], StringSplitOptions.RemoveEmptyEntries).Select(Plain).OfType<string>();

    /// <summary><paramref name="written"/> as the name of a rule's selector: whole text, and none where an expression of the markup writes it.</summary>
    private static string? Plain(string written)
    {
        var name = written.Trim();
        return name.Length == 0 || name.AsSpan().IndexOfAny('{', '}', '@') >= 0 ? null : ExtractText.Whole(name);
    }

    /// <summary><paramref name="value"/> as an expression: past a Razor <c>@</c>, an arrow function's head, the block its body opens and an <c>await</c>.</summary>
    private static string Body(string value)
    {
        var text = value.Trim().TrimStart('@').Trim();

        if (Arrow().Match(text) is { Success: true } arrow)
        {
            text = text[arrow.Length..].TrimStart().TrimStart('{').TrimStart();
        }

        return text.StartsWith("await ", StringComparison.Ordinal) ? text[6..].TrimStart() : text;
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

    /// <summary>What a Vue attribute is: a directive by its name, its argument and modifiers past it (<c>v-model:title.trim</c> a <c>v-model</c>).</summary>
    private static Role VueRole(MarkupAttribute attribute)
    {
        var name = attribute.Name;

        if (name.StartsWith('@') || name == "v-on" || name.StartsWith("v-on:", StringComparison.Ordinal))
        {
            return Role.Handler;
        }

        if (name.StartsWith(':') || name.StartsWith('.'))
        {
            return Role.Binding;
        }

        if (name.StartsWith('#'))
        {
            return Role.Declares;
        }

        if (!name.StartsWith("v-", StringComparison.Ordinal))
        {
            return Role.None;
        }

        return (name.IndexOfAny([':', '.']) is var end and > 0 ? name[..end] : name) switch
        {
            "v-for" => Role.Loop,
            "v-slot" => Role.Declares,
            _ => Role.Binding,
        };
    }

    /// <summary>What a Razor attribute is: a directive attribute by its name and modifier, another by whether its value is written as an expression.</summary>
    private static Role RazorRole(MarkupAttribute attribute)
    {
        var name = attribute.Name;

        if (!name.StartsWith('@'))
        {
            return attribute.Value.TrimStart().StartsWith('@') ? Role.Binding : Role.None;
        }

        var colon = name.IndexOf(':', StringComparison.Ordinal);
        var directive = colon < 0 ? name : name[..colon];
        var modifier = colon < 0 ? null : name[(colon + 1)..];

        return directive.StartsWith("@on", StringComparison.Ordinal) ? modifier is null ? Role.Handler : Role.None
            : directive == "@bind" || directive.StartsWith("@bind-", StringComparison.Ordinal) ? modifier switch
            {
                null or "get" => Role.Binding,
                "after" or "set" => Role.Handler,
                _ => Role.None,
            }
            : modifier is null ? Role.Binding : Role.None;
    }

    /// <summary>An arrow function's head: its parameters and its arrow.</summary>
    [GeneratedRegex(@"^(?:async\s+)?(?:\([^()]*\)|[A-Za-z_$][A-Za-z0-9_$]*)\s*=>", RegexOptions.CultureInvariant)]
    private static partial Regex Arrow();

    /// <summary>The name an expression's text begins with, nothing before it.</summary>
    [GeneratedRegex(@"^(?<name>[A-Za-z_$][A-Za-z0-9_$]*)", RegexOptions.CultureInvariant)]
    private static partial Regex Leading();

    /// <summary>The first name an expression begins with, past a <c>!</c> and an opening parenthesis.</summary>
    [GeneratedRegex(@"^[!(\s]*(?<name>[A-Za-z_$][A-Za-z0-9_$]*)", RegexOptions.CultureInvariant)]
    private static partial Regex First();

    /// <summary>A loop's value as Vue reads it: its aliases, <c>in</c> or <c>of</c>, and the expression of the list it walks.</summary>
    [GeneratedRegex(@"^\s*(?<alias>[\s\S]*?)\s+(?:in|of)\s+(?<list>\S[\s\S]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex Looped();

    /// <summary>A name a pattern binds: one no property key is - none a <c>:</c> follows - and no part of a longer name.</summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9_$])(?<name>[A-Za-z_$][A-Za-z0-9_$]*)(?![A-Za-z0-9_$])(?!\s*:)", RegexOptions.CultureInvariant)]
    private static partial Regex Bound();

    /// <summary>A pattern's default value, up to the next of its names: no name it binds.</summary>
    [GeneratedRegex(@"=[^,}\])]*", RegexOptions.CultureInvariant)]
    private static partial Regex Defaultless();
}
