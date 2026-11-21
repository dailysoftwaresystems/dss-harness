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

    private MarkupDialect(bool closesAsHtml, Func<MarkupElement, (string Name, ExtractQualifier? Qualifier)?> component, Func<MarkupAttribute, MarkupRole> role)
    {
        ClosesAsHtml = closesAsHtml;
        _component = component;
        _role = role;
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
    public static MarkupDialect Vue { get; } = new(closesAsHtml: false, element => Named(element.Tag) ?? (element.Name.Contains('-', StringComparison.Ordinal) ? (Pascal(element.Name), null) : null), VueRole);

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
    internal static (string Aliases, string List, int ListAt)? Loop(string value)
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
    private static string? Plain(string written)
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
