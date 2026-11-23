using System.Text.RegularExpressions;
using DssHarness.Core.Text;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// What one element of markup says, read alike in every markup a file holds - an HTML page, a Vue component's template,
/// an Angular template, a Razor file's markup, a JSX element's attributes - with each kind's own syntax a table over it.
/// Every kind says the same of a class and an id (<see cref="Selected"/>): each class a <c>uses</c> of the rule
/// <c>.name</c>, split as HTML splits a class list, at ASCII white space, and an id a <c>uses</c> of <c>#id</c>, both looked
/// for among rules alone (<see cref="CodeQualifierKinds.Selector"/>); the attributes a kind writes a class list and an id
/// in are its table's. Each kind says which tag is a component's - an <c>instantiates</c> of it - and what each attribute's
/// value is to it (<see cref="MarkupRole"/>): a handler's code, a bound expression, a loop or a structural directive, the
/// names a declaring attribute declares and over what, or nothing the graph reads; and, where its framework writes them,
/// how an event's value compiles, how an expression's pipes are read, which globals its compiler leaves alone, whether a
/// directive's argument is code, whether an attribute's value interpolates, which tags its framework builds in and which
/// control-flow blocks its text writes. What that code calls, uses and declares is never the dialect's to say but its
/// language's reader's: each markup's reader gives it the code as its framework compiles the value
/// (<see cref="EmbeddedCode.Script"/>).
/// </summary>
internal sealed class MarkupDialect
{
    /// <summary>The globals Vue's template compiler leaves to themselves, as its own list has them: no member of the component.</summary>
    private static readonly HashSet<string> VueGlobals = new(StringComparer.Ordinal)
    {
        "Infinity", "undefined", "NaN", "isFinite", "isNaN", "parseFloat", "parseInt", "decodeURI", "decodeURIComponent", "encodeURI",
        "encodeURIComponent", "Math", "Number", "Date", "Array", "Object", "Boolean", "String", "RegExp", "Map", "Set", "JSON", "Intl", "BigInt",
        "console", "Error", "Symbol",
    };

    /// <summary>The elements Angular builds in, which instantiate no component: what structures a template.</summary>
    private static readonly HashSet<string> AngularBuiltIns = new(StringComparer.Ordinal) { "ng-container", "ng-template", "ng-content" };

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
    /// that name; an <c>on*</c> attribute a handler, the body of the function HTML makes of it; a class a tag of another template
    /// engine is written into - a <c>{{ }}</c>, a <c>{% %}</c>, a <c>{# #}</c> (<see cref="HtmlExtractor.EngineAny"/>) - no class
    /// of its list; its elements closing as HTML closes them.
    /// </summary>
    public static MarkupDialect Html { get; } = new(
        closesAsHtml: true,
        element => element.Name.Contains('-', StringComparison.Ordinal) ? (element.Name, null) : null,
        attribute => attribute.Name.Length > 2 && attribute.Name.StartsWith("on", StringComparison.Ordinal) && !attribute.Name.Contains('-', StringComparison.Ordinal) ? MarkupRole.Handler : MarkupRole.None)
    {
        Tags = HtmlExtractor.EngineAny,
    };

    /// <summary>
    /// A Vue template's: a component's tag - PascalCase as written, or with a dash, which names the component its
    /// PascalCase form does, as Vue resolves <c>&lt;foo-bar&gt;</c> and <c>&lt;FooBar&gt;</c> alike - an instantiates;
    /// a <c>v-on:x</c> or <c>@x</c> attribute, its modifiers among them (<c>@click.prevent</c>), a handler - a member path a
    /// call of it with what the event gives; a <c>v-bind:x</c>, <c>:x</c> or <c>.x</c> attribute a binding, as is every
    /// other directive whose value is an expression - <c>v-model</c>, <c>v-if</c>, <c>v-show</c>, a <c>v-bind</c> or
    /// <c>v-on</c> of an object, a directive of the component's own; a directive's dynamic argument - <c>:[key]</c> - code;
    /// a <c>v-for</c> a loop over the list its expression names, its aliases names of its own; a <c>v-slot</c> or <c>#x</c>
    /// one that declares the names its value writes for what its element holds, the value read as a function's parameters;
    /// Vue's globals (<c>Math</c>, <c>JSON</c>) no member of the component; its tags matched as written.
    /// </summary>
    public static MarkupDialect Vue { get; } = new(closesAsHtml: false, element => Named(element.Tag) ?? (element.Name.Contains('-', StringComparison.Ordinal) ? (Pascal(element.Name), null) : null), VueRole)
    {
        BoundBy = VueLoop,
        HandledBy = VueHandler,
        DeclaredBy = VueSlot,
        DynamicArguments = true,
        Globals = VueGlobals,
    };

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

    /// <summary>
    /// An Angular template's: a component's selector - a tag with a dash, <c>&lt;app-cart&gt;</c>, the component whose
    /// <c>selector</c> it is - an instantiates, but the framework's own <c>ng-container</c>, <c>ng-template</c> and
    /// <c>ng-content</c>; a <c>(x)</c> or <c>on-x</c> attribute a handler, statements its event runs; a <c>[x]</c>,
    /// <c>bind-x</c> or <c>[(x)]</c> attribute a binding, and a <c>[class.x]</c> one a use of the rule <c>.x</c> besides; a
    /// structural directive's - <c>*ngIf</c>, <c>*ngFor</c>, any <c>*x</c> - its microsyntax read (<see cref="AngularStructural"/>):
    /// its expressions code, its <c>let</c> and <c>as</c> names its own; a <c>#x</c> or <c>ref-x</c> attribute a name declared
    /// over the whole view its element stands in, and a <c>let-x</c> one over what its element holds; each <c>{{ }}</c> an
    /// attribute's value writes code; each expression's pipes - <c>| date</c>, <c>| money: 'USD'</c> - an instantiates of the
    /// pipe by its name, its operand and its arguments code (<see cref="AngularExpression"/>); no globals; the control-flow
    /// blocks its text writes - <c>@if</c>, <c>@for</c>, <c>@switch</c>, <c>@defer</c>, <c>@let</c> and the rest; its
    /// elements closing as HTML closes them, a component's tag closed by its <c>/&gt;</c> as Angular closes it.
    /// </summary>
    public static MarkupDialect Angular { get; } = new(
        closesAsHtml: true,
        element => element.Name.Contains('-', StringComparison.Ordinal) && !AngularBuiltIns.Contains(element.Name) ? (element.Name, null) : null,
        AngularRole)
    {
        ClosesSelfBy = tag => tag.Contains('-', StringComparison.Ordinal),
        BoundBy = AngularStructural,
        BlocksBy = AngularBlocks,
        ExpressionBy = AngularExpression,
        DeclaredBy = AngularDeclared,
        OpensViewBy = element => element.Attributes.Any(attribute => attribute.Name.Length > 1 && attribute.Name[0] == '*'),
        HoldsViewBy = element => element.Name == "ng-template",
        ClassBound = AngularClassBound,
        Tags = MarkupTemplates.Mustaches,
        InterpolatesAttributes = true,
    };

    /// <summary>A JSX element's, in a script: a <c>className</c> or a <c>class</c> a class list, an <c>id</c> an id - the attributes' names as written, for JSX's are matched so.</summary>
    public static MarkupDialect Jsx { get; } = new(closesAsHtml: false, _ => null, _ => MarkupRole.None)
    {
        ClassAttributes = ["class", "className"],
    };

    /// <summary>Whether its elements close as HTML closes a page's, where their end tags are left out; else as written.</summary>
    public bool ClosesAsHtml { get; }

    /// <summary>The qualifier of a selector: the name, written as a rule writes it, is looked for among rules alone.</summary>
    public static ExtractQualifier Selector { get; } = new(CodeQualifierKinds.Selector);

    /// <summary>The attributes, by their names as the kind compares them, a class list is written in.</summary>
    private IReadOnlyList<string> ClassAttributes { get; init; } = ["class"];

    /// <summary>The attributes, by their names as the kind compares them, an id is written in.</summary>
    private IReadOnlyList<string> IdAttributes { get; init; } = ["id"];

    /// <summary>
    /// The tags its attribute values write that its markup's own expressions or another engine's are written in - Angular's
    /// <c>{{ }}</c>, and on a page another engine's <c>{{ }}</c>, <c>{% %}</c> and <c>{# #}</c>: each one piece with the name it is
    /// written into, whatever white space it holds, so a name a tag is written into is no class of a list
    /// (<see cref="Classes"/>); none where its values write none.
    /// </summary>
    private Regex? Tags { get; init; }

    /// <summary>Whether each <c>{{ }}</c> an attribute's value writes is code its framework reads - Angular's <c>title="{{ name }}"</c>.</summary>
    public bool InterpolatesAttributes { get; private init; }

    /// <summary>Whether a directive's dynamic argument - <c>:[key]</c>, <c>@[event]</c>, <c>#[slot]</c> - is code, as Vue's compiler reads it.</summary>
    public bool DynamicArguments { get; private init; }

    /// <summary>The names its template compiler leaves to themselves, no member of the component: Vue's globals; none of Angular's.</summary>
    public IReadOnlySet<string> Globals { get; private init; } = new HashSet<string>(StringComparer.Ordinal);

    private Func<string, bool>? ClosesSelfBy { get; init; }

    private Func<string, MarkupBinding?>? BoundBy { get; init; }

    private Func<MarkupText, IEnumerable<MarkupBlock>>? BlocksBy { get; init; }

    private Func<string, MarkupExpression>? ExpressionBy { get; init; }

    private Func<string, string> HandledBy { get; init; } = Statements;

    private Func<MarkupAttribute, MarkupDeclaration?>? DeclaredBy { get; init; }

    private Func<MarkupElement, bool>? OpensViewBy { get; init; }

    private Func<MarkupElement, bool>? HoldsViewBy { get; init; }

    private Func<string, string?>? ClassBound { get; init; }

    /// <summary>
    /// Whether an element of the tag <paramref name="tag"/>, as HTML names it, is closed by its <c>/&gt;</c> though the kind's
    /// elements close as HTML closes them - Angular's component's, a tag with a dash; none of a page's, which HTML leaves open.
    /// </summary>
    public bool ClosesSelf(string tag) => ClosesSelfBy?.Invoke(tag) == true;

    /// <summary>
    /// A loop's or a structural directive's value as this kind of markup reads it (<see cref="MarkupRole.Loop"/>): the names it
    /// declares for its element and the code it writes; <see langword="null"/> where the value is no loop, or the kind's markup
    /// writes none.
    /// </summary>
    public MarkupBinding? Bound(string value) => BoundBy?.Invoke(value);

    /// <summary>
    /// The control-flow blocks this kind's markup writes in the texts of <paramref name="markup"/>, in the order they are
    /// written - none where the kind writes none (<see cref="MarkupBlock"/>).
    /// </summary>
    public IEnumerable<MarkupBlock> Blocks(MarkupText markup) => BlocksBy?.Invoke(markup) ?? [];

    /// <summary>An expression the markup writes - a binding's, a <c>{{ }}</c>'s - as its framework compiles it: the code, and each pipe it names.</summary>
    public MarkupExpression Expression(string written) => ExpressionBy?.Invoke(written) ?? new MarkupExpression(written, []);

    /// <summary>The JavaScript an event attribute's value <paramref name="value"/> is compiled to, as this kind's compiler writes it.</summary>
    public string Handler(string value) => HandledBy(value);

    /// <summary>The names the declaring attribute <paramref name="attribute"/> (<see cref="MarkupRole.Declares"/>) declares, and over what; <see langword="null"/> where it declares none.</summary>
    public MarkupDeclaration? Declaration(MarkupAttribute attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        return DeclaredBy?.Invoke(attribute);
    }

    /// <summary>Whether <paramref name="element"/> is a view of its own, itself and what it holds - Angular's element a structural directive is written on.</summary>
    public bool OpensView(MarkupElement element) => OpensViewBy?.Invoke(element) == true;

    /// <summary>Whether what <paramref name="element"/> holds is a view of its own, the element itself the one around - Angular's <c>ng-template</c>.</summary>
    public bool HoldsView(MarkupElement element) => HoldsViewBy?.Invoke(element) == true;

    /// <summary>
    /// What <paramref name="element"/> itself says, each a reference made from <paramref name="from"/>: the component its
    /// tag makes, and its classes and its id uses of the rules that style them - a class a binding names among them
    /// (<c>[class.x]</c>). What its attributes' code says is its language's reader's (<see cref="Role"/>).
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
            foreach (var selector in Selected(attribute.Name, attribute.Value))
            {
                references.Add(new ExtractReference(from, CodeEdgeKinds.Uses, selector, attribute.Line) { Qualifier = Selector });
            }

            if (ClassBound?.Invoke(attribute.Written) is { } bound)
            {
                references.Add(new ExtractReference(from, CodeEdgeKinds.Uses, $".{bound}", attribute.Line) { Qualifier = Selector });
            }
        }
    }

    /// <summary>
    /// The rules an attribute named <paramref name="name"/> - as this kind compares names - whose value is <paramref name="value"/>
    /// names, each as a rule writes it: each class of a class list (<see cref="Classes"/>), <c>.name</c>, and an id, <c>#id</c>;
    /// none for an attribute of another name. The one rule of a class and an id, every markup's, a JSX element's among them.
    /// </summary>
    public IEnumerable<string> Selected(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);

        if (ClassAttributes.Contains(name, StringComparer.Ordinal))
        {
            return Classes(value, Tags).Select(named => $".{named}");
        }

        return IdAttributes.Contains(name, StringComparer.Ordinal) && Plain(value) is { } id ? [$"#{id}"] : [];
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
    /// The classes a class attribute holds, split at ASCII white space as HTML's class list is; a name the markup's own
    /// expressions write - an <c>@x</c>, a <c>{{ }}</c> - none: each tag <paramref name="tags"/> matches - <c>{{ }}</c>, another
    /// engine's <c>{% if x %}</c> - and the name it is written into are one piece, its white space none of the list's, and no
    /// class, for what the engine renders there no reader of the page knows. Each tag is found once, the value walked once.
    /// </summary>
    internal static IEnumerable<string> Classes(string value, Regex? tags = null)
    {
        var pieces = new List<string>();
        var start = -1;
        var tag = tags?.Match(value);

        for (var at = 0; at <= value.Length; at++)
        {
            if (tag is { Success: true } && tag.Index == at)
            {
                start = start < 0 ? at : start;
                at = tag.Index + tag.Length - 1;
                tag = tag.NextMatch();
                continue;
            }

            if (at == value.Length || value[at] is ' ' or '\t' or '\n' or '\f' or '\r')
            {
                if (start >= 0)
                {
                    pieces.Add(value[start..at]);
                }

                start = -1;
                continue;
            }

            start = start < 0 ? at : start;
        }

        return pieces.Select(Plain).OfType<string>();
    }

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

    /// <summary>Statements an event runs, its value <c>$event</c>: how Angular compiles every event's value, and Vue one that is no member path or function.</summary>
    private static string Statements(string value) => $"(function ($event) {{ {value} }});";

    // ---- Vue ----

    /// <summary>
    /// A loop's value as Vue reads a <c>v-for</c>'s: its aliases - the parentheses around them taken off, as Vue's compiler
    /// takes them off - and the expression of the list it walks, read around the element; <see langword="null"/> where the
    /// value is no loop.
    /// </summary>
    private static MarkupBinding? VueLoop(string value)
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

        return new MarkupBinding([.. JsReading.Binds(aliases)], [new MarkupCode(loop.Groups["list"].Index, loop.Groups["list"].Value)], []);
    }

    /// <summary>
    /// The JavaScript a Vue event attribute's value is compiled to, as Vue's compiler writes it: a member path - <c>pay</c>,
    /// <c>cart.add</c> - the handler it names, called with what the event gives; a function expression the handler itself;
    /// anything else statements the event runs, its value <c>$event</c>.
    /// </summary>
    private static string VueHandler(string value)
        => MemberPath.IsMatch(value) ? $"((...args) => {value}(...args));"
            : FunctionExpression.IsMatch(value) ? $"({value});"
            : Statements(value);

    /// <summary>A Vue slot's names: what its value's pattern binds, over what its element holds, the pattern read as a function's parameters so its defaults are code.</summary>
    private static MarkupDeclaration? VueSlot(MarkupAttribute attribute)
        => string.IsNullOrWhiteSpace(attribute.Value) ? null : new MarkupDeclaration([.. JsReading.Binds(attribute.Value)], View: false, Pattern: attribute.Value);

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

    /// <summary>A loop's value as Vue's compiler reads it: its aliases, <c>in</c> or <c>of</c>, and the expression of the list it walks.</summary>
    private static readonly Regex Looped = LinearRegex.Compile(@"^\s*(?<alias>[\s\S]*?)\s+(?:in|of)\s+(?<list>\S[\s\S]*)$");

    /// <summary>A member path, as a framework's compiler tells a handler an event attribute names from code it runs: names, each after a dot, an optional chain's <c>?.</c> or between brackets.</summary>
    private static readonly Regex MemberPath = LinearRegex.Compile(@"^\s*[A-Za-z_$][\w$]*(?:\??\.[A-Za-z_$][\w$]*|\['[^']*?'\]|\[""[^""]*?""\]|\[\d+\]|\[[A-Za-z_$][\w$]*\])*\s*$");

    /// <summary>A function expression, as a framework's compiler tells one an event attribute gives: an arrow's head, or a <c>function</c>'s.</summary>
    private static readonly Regex FunctionExpression = LinearRegex.Compile(@"^\s*(?:async\s*)?(?:\([^)]*?\)|[\w$]+)\s*(?::[^=]+)?=>|^\s*(?:async\s+)?function(?:\s+[\w$]+)?\s*\(");

    // ---- Razor ----

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

    // ---- Angular ----

    /// <summary>The pipes Angular's common package declares, which no component of the repository is: no reference of a pipe.</summary>
    private static readonly HashSet<string> AngularPipes = new(StringComparer.Ordinal)
    {
        "async", "currency", "date", "number", "i18nPlural", "i18nSelect", "json", "keyvalue", "lowercase", "percent", "slice", "titlecase", "uppercase",
    };

    /// <summary>The names a <c>@for</c>'s body is given whatever its header declares, as Angular gives them.</summary>
    private static readonly string[] AngularForContext = ["$index", "$first", "$last", "$even", "$odd", "$count"];

    /// <summary>
    /// What an Angular attribute is, by its name as HTML names it, in lower case so a directive's is read alike however it is
    /// cased: an event's - <c>(click)</c>, <c>on-click</c>; a binding's - <c>[x]</c>, <c>bind-x</c>, <c>[(x)]</c>, and a bare
    /// <c>ngIf</c>, <c>ngSwitch</c> or <c>ngTemplate</c>; a structural directive's - any <c>*x</c>, and a bare <c>ngFor</c>; a name
    /// declared - <c>#x</c>, <c>ref-x</c>, <c>let-x</c>; HTML's own, nothing. HTML's <c>on*=</c> is no Angular mark: a page's own.
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

        if ((name.Length > 1 && name[0] == '*') || name == "ngfor")
        {
            return MarkupRole.Loop;
        }

        if (name is "ngif" or "ngswitch" or "ngtemplate")
        {
            return MarkupRole.Binding;
        }

        return name.Length > 1 && (name[0] == '#' || name.StartsWith("ref-", StringComparison.Ordinal) || name.StartsWith("let-", StringComparison.Ordinal)) ? MarkupRole.Declares : MarkupRole.None;
    }

    /// <summary>
    /// The name an Angular attribute declares in its own name, as the text writes it, in its case: a <c>#x</c> or <c>ref-x</c> -
    /// a template reference - over the whole view its element stands in, as Angular sees one from anywhere in it; a <c>let-x</c>
    /// over what its element, an <c>ng-template</c>, holds. Its value names what it refers to, no code.
    /// </summary>
    private static MarkupDeclaration? AngularDeclared(MarkupAttribute attribute)
    {
        var written = attribute.Written;
        var (name, view) = written.StartsWith("ref-", StringComparison.Ordinal) ? (written[4..], true)
            : written.StartsWith("let-", StringComparison.Ordinal) ? (written[4..], false)
            : written.Length > 0 && written[0] == '#' ? (written[1..], true)
            : (string.Empty, false);

        return IsIdentifier(name) ? new MarkupDeclaration([name], view, Pattern: null) : null;
    }

    /// <summary>The class a <c>[class.x]</c> binding sets, as the text writes it; <see langword="null"/> for any other attribute.</summary>
    private static string? AngularClassBound(string written)
        => written.Length > 8 && written.StartsWith("[class.", StringComparison.Ordinal) && written[^1] == ']' && Plain(written[7..^1]) is { } named ? named : null;

    /// <summary>
    /// An Angular expression as its compiler reads it: each pipe - every <c>|</c>, for Angular writes no bitwise or - written
    /// out of the code, its bar and its name blank and each colon before an argument a <c>+</c>, so the operand and the
    /// arguments are what the code reads and every place stays where the text writes it; each pipe, by its name and where its
    /// name begins, but those Angular's common package declares.
    /// </summary>
    private static MarkupExpression AngularExpression(string written)
    {
        if (written.IndexOf('|', StringComparison.Ordinal) < 0)
        {
            return new MarkupExpression(written, []);
        }

        var code = written.ToCharArray();
        var pipes = new List<MarkupPipe>();

        // At each depth of brackets: whether a pipe's arguments are being read there, and how many conditionals are open.
        var piped = new List<bool> { false };
        var conditionals = new List<int> { 0 };

        for (var at = 0; at < written.Length; at++)
        {
            var character = written[at];
            var next = at + 1 < written.Length ? written[at + 1] : '\0';
            var level = piped.Count - 1;

            switch (character)
            {
                case '\'' or '"' or '`':
                    at = StringEnd(written, at);
                    break;

                case '(' or '[' or '{':
                    piped.Add(false);
                    conditionals.Add(0);
                    break;

                case ')' or ']' or '}' when level > 0:
                    piped.RemoveAt(level);
                    conditionals.RemoveAt(level);
                    break;

                case '?' when next is '?' or '.':
                    at++;
                    break;

                case '?':
                    conditionals[level]++;
                    break;

                case '|' when next == '|':
                    at++;
                    break;

                case '|':
                    code[at] = ' ';
                    var name = at + 1;

                    for (; name < written.Length && char.IsWhiteSpace(written[name]); name++)
                    {
                    }

                    var past = name;

                    for (; past < written.Length && (char.IsAsciiLetterOrDigit(written[past]) || written[past] is '_' or '$'); past++)
                    {
                        code[past] = ' ';
                    }

                    if (past > name && !AngularPipes.Contains(written[name..past]))
                    {
                        pipes.Add(new MarkupPipe(written[name..past], name));
                    }

                    piped[level] = true;
                    at = past - 1;
                    break;

                case ':' when conditionals[level] > 0:
                    conditionals[level]--;
                    break;

                case ':' when piped[level]:
                    code[at] = '+';
                    break;
            }
        }

        return new MarkupExpression(new string(code), pipes);
    }

    /// <summary>Where the string whose quote is at <paramref name="at"/> ends: at its closing quote, a backslash escaping the character after it; at the text's end where it is left open.</summary>
    private static int StringEnd(string text, int at)
    {
        var quote = text[at];

        for (var end = at + 1; end < text.Length; end++)
        {
            if (text[end] == '\\')
            {
                end++;
            }
            else if (text[end] == quote)
            {
                return end;
            }
        }

        return text.Length - 1;
    }

    /// <summary>
    /// A structural directive's value as Angular reads its microsyntax - <c>let item of items; index as i; trackBy: trackItem</c>,
    /// <c>user$ | async as user; else loading</c>, <c>cond; then a else b</c>: its first expression code, read around the element;
    /// each <c>let x</c> (<c>let i = index</c>) and each <c>as x</c> a name of its own; each key's expression after it - <c>of</c>'s,
    /// <c>trackBy</c>'s, <c>else</c>'s, <c>context</c>'s - code, read around the element; a key named by <c>as</c> (<c>index as i</c>)
    /// the context's, no code. A <c>@if</c>'s and a <c>@switch</c>'s header is read alike.
    /// </summary>
    private static MarkupBinding? AngularStructural(string value)
    {
        var aliases = new List<string>();
        var outer = new List<MarkupCode>();
        var first = true;
        var at = 0;

        while (true)
        {
            for (; at < value.Length && (char.IsWhiteSpace(value[at]) || value[at] is ';' or ','); at++)
            {
            }

            if (at >= value.Length)
            {
                break;
            }

            if (WordAt(value, at, "let"))
            {
                // let x, or let i = index: a name of its own, what it is given the context's.
                var (name, past) = Identifier(value, Blank(value, at + 3));

                if (name is null)
                {
                    at = ExpressionEnd(value, at + 3);
                    continue;
                }

                aliases.Add(name);
                at = Blank(value, past);

                if (at < value.Length && value[at] == '=')
                {
                    at = Identifier(value, Blank(value, at + 1)).Past;
                }

                first = false;
                continue;
            }

            if (WordAt(value, at, "as"))
            {
                // A name the expression before it is given, written after a separator: @if (cond; as alias).
                var (alias, past) = Identifier(value, Blank(value, at + 2));
                aliases.AddRange(alias is null ? [] : [alias]);
                at = Math.Max(past, at + 2);
                continue;
            }

            if (!first && Identifier(value, at) is ({ }, var keyPast))
            {
                // A key: its expression after it, or, where an as follows it, the context's key named.
                at = Blank(value, keyPast);
                at = at < value.Length && value[at] == ':' ? Blank(value, at + 1) : at;

                if (WordAt(value, at, "as"))
                {
                    var (alias, past) = Identifier(value, Blank(value, at + 2));
                    aliases.AddRange(alias is null ? [] : [alias]);
                    at = Math.Max(past, at + 2);
                    continue;
                }

                if (at >= value.Length)
                {
                    break;
                }
            }

            var end = ExpressionEnd(value, at);

            if (!string.IsNullOrWhiteSpace(value[at..end]))
            {
                outer.Add(new MarkupCode(at, value[at..end]));
            }

            at = end;
            first = false;
        }

        return aliases.Count == 0 && outer.Count == 0 ? null : new MarkupBinding(aliases, outer, []);
    }

    /// <summary>
    /// A <c>@for</c>'s header as Angular reads it - <c>item of items; track item.id; let i = $index, e = $even</c>: its alias and the
    /// names each <c>let</c> declares the body's, with the context's own (<c>$index</c> and the rest); the list's expression code
    /// read around the block, and the <c>track</c> expression code read where its alias is declared.
    /// </summary>
    private static MarkupBinding? AngularFor(string value)
    {
        var (alias, past) = Identifier(value, Blank(value, 0));
        var of = Blank(value, past);

        if (alias is null || !WordAt(value, of, "of"))
        {
            return null;
        }

        var aliases = new List<string> { alias };
        var outer = new List<MarkupCode>();
        var inner = new List<MarkupCode>();
        var at = Blank(value, of + 2);
        var end = ExpressionEnd(value, at);

        outer.AddRange(string.IsNullOrWhiteSpace(value[at..end]) ? [] : [new MarkupCode(at, value[at..end])]);
        at = end;

        while (at < value.Length)
        {
            for (; at < value.Length && (char.IsWhiteSpace(value[at]) || value[at] == ';'); at++)
            {
            }

            if (WordAt(value, at, "track"))
            {
                var from = Blank(value, at + 5);
                end = ExpressionEnd(value, from);
                inner.AddRange(string.IsNullOrWhiteSpace(value[from..end]) ? [] : [new MarkupCode(from, value[from..end])]);
                at = Math.Max(end, from);
                continue;
            }

            if (WordAt(value, at, "let"))
            {
                // let i = $index, e = $even: each a name of the body's, given the context's.
                at += 3;

                while (Identifier(value, Blank(value, at)) is ({ } name, var named))
                {
                    aliases.Add(name);
                    at = Blank(value, named);
                    at = at < value.Length && value[at] == '=' ? Identifier(value, Blank(value, at + 1)).Past : at;
                    at = Blank(value, at);

                    if (at >= value.Length || value[at] != ',')
                    {
                        break;
                    }

                    at++;
                }

                continue;
            }

            if (at < value.Length)
            {
                at = Math.Max(ExpressionEnd(value, at), at + 1);
            }
        }

        aliases.AddRange(AngularForContext);
        return new MarkupBinding(aliases, outer, inner);
    }

    /// <summary>
    /// A <c>@defer</c>'s header as Angular reads it - <c>on viewport; when isReady; prefetch when soon</c>: each <c>when</c>'s
    /// condition code, a <c>prefetch</c>'s and a <c>hydrate</c>'s among them; an <c>on</c> trigger names an event and a template's
    /// element, no code.
    /// </summary>
    private static MarkupBinding? AngularDefer(string value)
    {
        var outer = new List<MarkupCode>();
        var at = 0;

        while (at < value.Length)
        {
            for (; at < value.Length && (char.IsWhiteSpace(value[at]) || value[at] == ';'); at++)
            {
            }

            at = WordAt(value, at, "prefetch") ? Blank(value, at + 8) : WordAt(value, at, "hydrate") ? Blank(value, at + 7) : at;

            if (WordAt(value, at, "when"))
            {
                var from = Blank(value, at + 4);
                var end = ExpressionEnd(value, from);
                outer.AddRange(string.IsNullOrWhiteSpace(value[from..end]) ? [] : [new MarkupCode(from, value[from..end])]);
                at = Math.Max(end, from);
                continue;
            }

            var stop = value.IndexOf(';', at);
            at = stop < 0 ? value.Length : stop + 1;
        }

        return outer.Count == 0 ? null : new MarkupBinding([], outer, []);
    }

    /// <summary>
    /// A <c>@let</c>'s declaration as Angular reads it - <c>total = price * qty</c>: its name the view's from where it stands, its
    /// value's expression code; <see langword="null"/> where it writes no name and <c>=</c>.
    /// </summary>
    private static MarkupBinding? AngularLet(string value)
    {
        var (name, past) = Identifier(value, Blank(value, 0));
        var equals = Blank(value, past);

        if (name is null || equals >= value.Length || value[equals] != '=')
        {
            return null;
        }

        var from = Blank(value, equals + 1);
        return new MarkupBinding([name], string.IsNullOrWhiteSpace(value[from..]) ? [] : [new MarkupCode(from, value[from..])], []);
    }

    /// <summary>
    /// Where the expression that begins at <paramref name="at"/> ends, as Angular's microsyntax ends one: at a <c>;</c> or a
    /// <c>,</c> outside its brackets and strings; or, after it has an operand, at a word that begins the next part - <c>as</c>,
    /// <c>let</c>, <c>else</c>, <c>then</c> - or a key and its colon, where no conditional waits for that colon.
    /// </summary>
    private static int ExpressionEnd(string value, int at)
    {
        var depth = 0;
        var conditionals = 0;

        for (var end = at; end < value.Length; end++)
        {
            var character = value[end];

            switch (character)
            {
                case '\'' or '"' or '`':
                    end = StringEnd(value, end);
                    continue;

                case '(' or '[' or '{':
                    depth++;
                    continue;

                case ')' or ']' or '}':
                    depth = Math.Max(depth - 1, 0);
                    continue;

                case ';' or ',' when depth == 0:
                    return end;

                case '?' when end + 1 < value.Length && value[end + 1] is '?' or '.':
                    end++;
                    continue;

                case '?' when depth == 0:
                    conditionals++;
                    continue;

                case ':' when depth == 0 && conditionals > 0:
                    conditionals--;
                    continue;
            }

            // A part's first word, after white space that follows an operand.
            if (depth == 0 && end > at && char.IsWhiteSpace(value[end - 1]) && IsIdentifierStart(character) && OperandBefore(value, at, end - 1))
            {
                if (WordAt(value, end, "as") || WordAt(value, end, "let") || WordAt(value, end, "else") || WordAt(value, end, "then"))
                {
                    return end;
                }

                var (_, past) = Identifier(value, end);
                var after = Blank(value, past);

                if (conditionals == 0 && after < value.Length && value[after] == ':' && (after + 1 >= value.Length || value[after + 1] != ':'))
                {
                    return end;
                }
            }
        }

        return value.Length;
    }

    /// <summary>Whether what stands last before the white space ending at <paramref name="before"/>, back to <paramref name="from"/>, ends an operand: a name's or a number's character, a closing bracket or a quote.</summary>
    private static bool OperandBefore(string value, int from, int before)
    {
        for (var at = before; at >= from; at--)
        {
            if (!char.IsWhiteSpace(value[at]))
            {
                return char.IsAsciiLetterOrDigit(value[at]) || value[at] is '_' or '$' or ')' or ']' or '}' or '\'' or '"' or '`';
            }
        }

        return false;
    }

    /// <summary>Whether the word <paramref name="word"/> stands whole at <paramref name="at"/>.</summary>
    private static bool WordAt(string value, int at, string word)
        => at >= 0 && at + word.Length <= value.Length && string.CompareOrdinal(value, at, word, 0, word.Length) == 0
            && (at + word.Length == value.Length || !IsIdentifierPart(value[at + word.Length]))
            && (at == 0 || !IsIdentifierPart(value[at - 1]));

    /// <summary>The identifier at <paramref name="at"/> and where it ends; none where none begins there.</summary>
    private static (string? Name, int Past) Identifier(string value, int at)
    {
        if (at >= value.Length || !IsIdentifierStart(value[at]))
        {
            return (null, at);
        }

        var past = at + 1;

        for (; past < value.Length && IsIdentifierPart(value[past]); past++)
        {
        }

        return (value[at..past], past);
    }

    /// <summary>Past the white space at <paramref name="at"/>.</summary>
    private static int Blank(string value, int at)
    {
        for (; at < value.Length && char.IsWhiteSpace(value[at]); at++)
        {
        }

        return at;
    }

    private static bool IsIdentifierStart(char character) => char.IsAsciiLetter(character) || character is '_' or '$';

    private static bool IsIdentifierPart(char character) => char.IsAsciiLetterOrDigit(character) || character is '_' or '$';

    /// <summary>Whether <paramref name="name"/> is a name a template declares: a JavaScript identifier of ASCII.</summary>
    private static bool IsIdentifier(string name) => name.Length > 0 && IsIdentifierStart(name[0]) && name.All(IsIdentifierPart);

    /// <summary>The words a control-flow block Angular's template writes begins with.</summary>
    private static readonly HashSet<string> BlocksOf = new(StringComparer.Ordinal)
    {
        "if", "else", "for", "switch", "case", "empty", "default", "defer", "placeholder", "loading", "error", "let",
    };

    /// <summary>
    /// The control-flow blocks Angular's template writes in the texts of <paramref name="markup"/> - each <c>@if</c>, an
    /// <c>@else if</c> read as one, <c>@for</c>, <c>@switch</c>, <c>@case</c>, <c>@empty</c>, <c>@default</c>, <c>@else</c>,
    /// <c>@defer</c>, <c>@placeholder</c>, <c>@loading</c>, <c>@error</c>, and each <c>@let</c> declaration - in the order
    /// written: what its header writes (<see cref="MarkupBinding"/>) and where its header begins, and the stretch its braces
    /// hold, each block ending where its braces close whatever blocks it holds. The braces are matched over the markup's texts
    /// alone - an element's attribute values never counted, for their braces are balanced within the element, and a script's
    /// or a style's text none of them (<see cref="MarkupText.Texts"/>) - and a <c>{{ }}</c>'s and an escaped <c>@{</c>'s are no
    /// block's.
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

                    if (Header(text, at, runEnd) is not { } header)
                    {
                        continue;
                    }

                    // An @else if is read as the @if it is, its condition code.
                    var kind = header.Kind == "else if" ? "if" : header.Kind;

                    if (!BlocksOf.Contains(kind))
                    {
                        continue;
                    }

                    var binding = header.Expression is not { } expression ? null
                        : kind switch
                        {
                            "for" => AngularFor(expression),
                            "if" or "switch" or "case" => AngularStructural(expression),
                            "defer" => AngularDefer(expression),
                            "let" => AngularLet(expression),
                            _ => null,
                        };

                    var block = new OpenBlock(at, kind, binding, header.ExpressionAt, header.Body);
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

        return [.. blocks.Select(block => new MarkupBlock(block.At, block.Kind, block.Binding, block.ExpressionAt, block.Body < 0 ? -1 : block.Body + 1, block.End))];
    }

    /// <summary>
    /// A block's header at the <c>@</c> at <paramref name="at"/>: its word, the expression its parentheses hold - a <c>@let</c>'s
    /// declaration to its <c>;</c> - and where that begins, and the brace its body begins at; <see langword="null"/> where no word follows.
    /// </summary>
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

        // @let name = expression; - a declaration of the view's, which no brace follows.
        if (kind == "let" && rest > word)
        {
            var stop = rest;

            for (var nested = 0; stop < end && (text[stop] != ';' || nested > 0); stop++)
            {
                nested += text[stop] is '(' or '[' or '{' ? 1 : text[stop] is ')' or ']' or '}' ? -1 : 0;
            }

            return (kind, text.Slice(rest, stop), rest, -1L, Math.Min(stop + 1, end));
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
    private sealed class OpenBlock(long at, string kind, MarkupBinding? binding, long expressionAt, long body)
    {
        public long At { get; } = at;

        public string Kind { get; } = kind;

        public MarkupBinding? Binding { get; } = binding;

        public long ExpressionAt { get; } = expressionAt;

        public long Body { get; } = body;

        /// <summary>How many braces are open once its own is: what tells its closing brace from one a block it holds closes.</summary>
        public int Depth { get; set; }

        /// <summary>Where its closing brace stands; -1 where none was read.</summary>
        public long End { get; set; } = -1;
    }
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

    /// <summary>A loop or a structural directive: the code it writes, read around the element, and the names it declares for the element (<see cref="MarkupDialect.Bound"/>).</summary>
    Loop,

    /// <summary>A declaring attribute's: the names it declares, over what its dialect says (<see cref="MarkupDialect.Declaration"/>).</summary>
    Declares,
}

/// <summary>One piece of code a value of markup writes, and where it begins in the value.</summary>
/// <param name="At">Where it begins in the value.</param>
/// <param name="Code">The code, as written.</param>
internal sealed record MarkupCode(int At, string Code);

/// <summary>
/// What a loop, a structural directive or a block's header writes: the names it declares for what it stands over, the code it
/// writes that is read around it - a list's expression, a condition - and the code read where its names are declared - a
/// <c>@for</c>'s <c>track</c>.
/// </summary>
/// <param name="Aliases">The names it declares.</param>
/// <param name="Outer">The code read around it, where its names are not declared.</param>
/// <param name="Inner">The code read where its names are declared.</param>
internal sealed record MarkupBinding(IReadOnlyList<string> Aliases, IReadOnlyList<MarkupCode> Outer, IReadOnlyList<MarkupCode> Inner);

/// <summary>A pipe an expression names, by its name, and where its name begins in the expression.</summary>
/// <param name="Name">The pipe's name, as a <c>@Pipe</c>'s <c>name</c> gives it.</param>
/// <param name="At">Where its name begins in the expression.</param>
internal sealed record MarkupPipe(string Name, int At);

/// <summary>An expression of markup as its framework compiles it: its code, of the same length as written, and each pipe it names.</summary>
/// <param name="Code">The code the language's reader reads.</param>
/// <param name="Pipes">The pipes it names.</param>
internal sealed record MarkupExpression(string Code, IReadOnlyList<MarkupPipe> Pipes);

/// <summary>The names a declaring attribute declares, and over what.</summary>
/// <param name="Names">The names.</param>
/// <param name="View">Whether they are seen over the whole view the element stands in - Angular's template reference - rather than over what the element holds.</param>
/// <param name="Pattern">The value read as a function's parameters where it is a pattern of code - a Vue slot's, whose defaults are code; <see langword="null"/> where it is none.</param>
internal sealed record MarkupDeclaration(IReadOnlyList<string> Names, bool View, string? Pattern);

/// <summary>
/// One control-flow block an Angular template writes in its text - <c>@if</c>, an <c>@else if</c> read as an <c>if</c>, <c>@for</c>,
/// <c>@switch</c>, <c>@case</c>, <c>@empty</c>, <c>@default</c>, <c>@else</c>, <c>@defer</c> and its <c>@placeholder</c>,
/// <c>@loading</c> and <c>@error</c>, and a <c>@let</c> - with what its header writes and where the header begins, and the stretch
/// its braces hold: from past its opening brace to its closing one; -1 where its braces are not read, and a <c>@let</c>'s, which
/// has none.
/// </summary>
/// <param name="At">Where its <c>@</c> stands.</param>
/// <param name="Kind">Its word: <c>if</c>, <c>else</c>, <c>for</c>, <c>switch</c>, <c>case</c>, <c>empty</c>, <c>default</c>, <c>defer</c>, <c>placeholder</c>, <c>loading</c>, <c>error</c> or <c>let</c>.</param>
/// <param name="Binding">What its header writes (<see cref="MarkupBinding"/>); <see langword="null"/> where it writes nothing the graph reads.</param>
/// <param name="ExpressionAt">Where its header's text begins, which each piece of <paramref name="Binding"/> is placed from; -1 where it has none.</param>
/// <param name="Body">Where its body begins, past its opening brace; -1 where its brace is not read.</param>
/// <param name="End">Where its body ends, at its closing brace; -1 where it is not read.</param>
internal sealed record MarkupBlock(long At, string Kind, MarkupBinding? Binding, long ExpressionAt, long Body, long End);
