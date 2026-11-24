using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;
using DssHarness.Core.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// A Razor view (<c>.cshtml</c>) or a Blazor component (<c>.razor</c>). A component is a <c>type</c> named by its file in
/// its <c>@namespace</c> - else the namespace its directory's path gives, each part made a C# name as Razor makes it,
/// none at the tree's root, which the linker reads in the nearest <c>_Imports.razor</c>'s <c>@namespace</c> above it
/// instead, each directory past that one a name after it, as Razor compiles it (<see cref="FileExtract.FolderScope"/>) -
/// generic by its <c>@typeparam</c>s, marked a part of its code-behind partial, which the linker joins to it; a view is a
/// <c>section</c> over its lines. The file is read as Razor's own parser reads it, in one walk
/// (<see cref="Syntax"/>), and the class Razor compiles it to is read by the C# reader as one class
/// (<see cref="EmbeddedCode.CSharp"/>): the code of its <c>@code</c> and <c>@functions</c> blocks its members, an
/// <c>@inject</c> a property of the type it is given, <c>@model</c> its <c>Model</c> property, <c>@inherits</c> and
/// <c>@implements</c> its bases, <c>@attribute</c> an attribute of it; and every piece of C# its markup holds - each
/// <c>@{ }</c> block and each statement, its <c>else</c>, <c>catch</c> and <c>finally</c> among it, each expression, each
/// directive attribute's value - code of the file's head that sees the class's members, a name the markup's code
/// declares where it stands - a statement's variables, a pattern's, a block's locals, a component's <c>context</c> -
/// its own over the stretch C# declares it for (<see cref="Names"/>). Each <c>@using</c> is read by the C# reader too, a
/// scope import - a <c>_ViewImports.cshtml</c>'s and an <c>_Imports.razor</c>'s reaching every file of the language
/// under their directory. Its markup - what is no markup taken out - is read by the markup reader every markup file
/// shares (<see cref="MarkupText"/>, <see cref="MarkupDialect.Razor"/>): a PascalCase or dotted element and a
/// <c>&lt;partial name=</c> an <c>instantiates</c>, as is an <c>Html.Partial("x")</c> and its kin its code calls. The C#
/// is embedded as every markup embeds code (<see cref="EmbeddedCode"/>).
/// </summary>
public sealed class RazorExtractor : ICodeGraphExtractor
{
    /// <summary>The names of the files whose <c>@using</c> reaches a whole directory's subtree.</summary>
    private static readonly HashSet<string> Imports = new(StringComparer.OrdinalIgnoreCase) { "_ViewImports.cshtml", "_Imports.razor" };

    /// <summary>The directives a line of Razor begins with: what it says of the file, never markup.</summary>
    private static readonly HashSet<string> DirectiveNames = new(StringComparer.Ordinal)
    {
        "using", "model", "inherits", "inject", "namespace", "layout", "attribute", "page", "implements", "typeparam", "preservewhitespace",
        "rendermode", "addTagHelper", "removeTagHelper", "tagHelperPrefix",
    };

    /// <summary>The statements Razor reads after an <c>@</c> as C#: each a word, a parenthesis where it has one, and a block.</summary>
    private static readonly HashSet<string> StatementWords = new(StringComparer.Ordinal) { "if", "for", "foreach", "while", "switch", "lock", "using", "do", "try" };

    /// <summary>The methods of a view's <c>Html</c> that render a partial view by its name.</summary>
    private static readonly HashSet<string> PartialMethods = new(StringComparer.Ordinal) { "Partial", "PartialAsync", "RenderPartial", "RenderPartialAsync" };

    /// <inheritdoc/>
    public string Language => "razor";

    /// <inheritdoc/>
    public int Version => 2;

    /// <inheritdoc/>
    public CodeGraphClaims Claims { get; } = CodeGraphClaims.OfExtensions(".cshtml", ".razor");

    /// <inheritdoc/>
    public bool Outlines => true;

    /// <inheritdoc/>
    public CodeGraphExtractorHelp Help { get; } = new(
        "Razor views (.cshtml) and Blazor components (.razor): a component as a type named by its file in its @namespace - else in the nearest _Imports.razor's @namespace above it, each directory past that one a name after it, as Razor compiles it, else in the namespace its directory's path gives, none at the tree's root - generic by its @typeparams, a part of its code-behind partial, a view as a section over its lines; the file read as Razor's parser reads it and the class it compiles to read by the C# reader - its @code and @functions blocks, an @inject a property of the type it is given, @model its Model property, @inherits and @implements its bases, @attribute an attribute of it - and every piece of C# its markup holds, each @{ } block, each @if, @foreach, @try and their kin with their else, catch and finally, each @x and @( ) expression, each directive attribute's value (@onclick, @bind), @layout's type and @page's route, read as code of the file's head that sees the class's members: what it calls, makes and uses - a name the markup's own code declares where it stands (a statement's or a pattern's variable, a @{ } block's local, a template's context - or the name its Context= gives - inside a component's content) the markup's and no member; @using read by the C# reader as a scope import - one a _ViewImports.cshtml or _Imports.razor holds reaching every file of the language under its directory, as its @namespace does; and its markup read by the reader every markup file shares: a PascalCase element, a partial name= and an Html.Partial(\"x\") as instantiates, and each class and id as a use of the rule that styles it",
        [
            ("tag-helpers", "what a tag helper is, and an element no component of the tree names: an instantiates that resolves nowhere, counted as such"),
            ("untyped-attributes", "a component attribute written without an @ whose parameter is no string - Count=\"Items.Count\" - read as text: the parameter's type is the component's, which the file does not say"),
            new CodeGraphLimit("huge-files", "a file of more than 2,147,483,647 characters, past the Int32 the HTML tokenizer (AngleSharp) counts a position in: nothing in it read, the line its count ends on marked dynamic, with why") { Signs = [MarkupText.HugeFile] },
            .. CSharpExtractor.CodeUnseen,
        ])
    {
        Holds = "AngleSharp's text source, which keeps every character the tokenizer decodes - its HTML tokenizer reads a source of its buffered kind alone (a TextSource over a stream), and the source that lets go of what it read is offered to its parser alone, which builds a document rather than the tokens this reader matches - so reading a file holds its markup's text once, in pieces, never as one string; and the C# compiler's syntax tree of each run of code, which the compiler is given as a string of its own and keeps whole, as the C# reader's says",
    };

    /// <inheritdoc/>
    public FileExtract Extract(CodeFileInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var text = input.Text.LineFed();

        if (MarkupText.Unread(text) is { } unread)
        {
            return unread;
        }

        var lines = text.LineIndex;
        var lineCount = input.Lines.Count;
        var name = CodeGraphIds.FileName(input.Path);
        var component = name.EndsWith(".razor", StringComparison.OrdinalIgnoreCase);
        var className = Identifier(name.LastIndexOf('.') is var dot and > 0 ? name[..dot] : name);
        var razor = Syntax.Of(text);
        var ownNamespace = razor.Directives.FirstOrDefault(directive => directive.Name == "namespace" && directive.Rest.Length > 0)?.Rest;
        var scope = ownNamespace ?? FolderNamespace(input.Path);
        var scopeName = scope is null ? null : QualifiedName.Of(scope);

        // A generic component's class is named as the C# reader names a generic one: by its count of type parameters.
        var typeParameters = razor.Directives.Where(directive => directive.Name == "typeparam").Select(directive => TypeParameter.Match(directive.Rest)).Where(match => match.Success).ToList();
        var qualified = ExtractText.Qualify(scopeName, typeParameters.Count > 0 ? $"{className}`{typeParameters.Count}" : className);

        // The head of the extract, which the markup's references and its code's are made from: a component's type, a view's section.
        ExtractNode? head = component
            ? new ExtractNode(qualified.Prepend($"{Language}:"), CodeNodeKinds.Type, className, qualified, 1, Math.Max(lineCount, 1), ExtractText.Signature(qualified.Whole())) { SubKind = "component", Part = true }
            : new ExtractNode($"{Language}:{input.Path}#view", CodeNodeKinds.Section, className, $"{input.Path}#view", 1, Math.Max(lineCount, 1), ExtractText.Signature(className)) { SubKind = "view" };
        var from = head.Id;

        // The markup with what is no markup taken out - comments, code, directives - so it is read where it stands.
        var markup = MarkupText.Read(EmbeddedCode.Blanked(text, razor.Code), MarkupDialect.Razor.ClosesAsHtml);
        var references = new List<ExtractReference>();
        var events = new List<Event>();

        foreach (var element in markup.Elements)
        {
            MarkupDialect.Razor.Read(element, from, references);

            // A component's content is a template of its own, which declares its context - by the name its Context= gives.
            if (component && MarkupDialect.Razor.Instantiated(element) && element.Close > element.OpenEnd)
            {
                events.Add(new Event(element.OpenEnd, EventKind.Holder, element.Close, [Unnamed(element.Attribute("context")?.Trim() is { Length: > 0 } named ? named : "context")], null));
            }

            // A directive attribute's value is C#, an @ before it as an expression's left out; a handler that names a
            // method is called by the component with what the event gives, as Razor compiles it.
            foreach (var attribute in element.Attributes.Where(attribute => attribute.Name.StartsWith('@') && MarkupDialect.Razor.Role(attribute) != MarkupRole.None && !string.IsNullOrWhiteSpace(attribute.Value)))
            {
                var value = attribute.Value;
                var at = attribute.ValueAt;
                var leading = value.Length - value.TrimStart().Length;

                if (value.AsSpan(leading).StartsWith("@"))
                {
                    at += leading + 1;
                    value = value[(leading + 1)..];
                }

                events.Add(MarkupDialect.Razor.Role(attribute) == MarkupRole.Handler && IsMethodGroup(SyntaxFactory.ParseExpression(value))
                    ? new Event(at, EventKind.Region, 0, [], new CodeRegion(from, string.Empty, at, $"{value}();") { Called = true })
                    : Region(at, $"_ = ({value});"));
            }
        }

        foreach (var (start, end) in razor.Sections)
        {
            events.Add(new Event(start, EventKind.Holder, end, [], null));
        }

        // The statements and the elements stand in the order the file writes them, so one walk over both finds each
        // component a statement's markup holds.
        var held = 0;

        foreach (var construct in razor.Constructs)
        {
            var code = Code(text, construct, component ? markup.Elements : [], ref held);
            var root = SyntaxFactory.ParseStatement($"{{{code}}}");

            foreach (var (declared, written, start, end) in Names(root))
            {
                events.Add(new Event(construct.Start + start - 1, EventKind.Declaration, end is { } stretch ? construct.Start + stretch - 1 : -1, [(declared, written)], null));
            }

            Partials(root, construct.Start - 1);
            events.Add(Region(construct.Start, code));
        }

        foreach (var (start, end) in razor.Expressions)
        {
            var code = text.Slice(start, end);

            Partials(SyntaxFactory.ParseExpression(code), start);
            events.Add(Region(start, $"_ = ({code});"));
        }

        // A layout a file names is a type its class is given, a page's route a string, a render mode a value.
        foreach (var directive in razor.Directives.Where(directive => directive.Rest.Length > 0))
        {
            if (directive.Name switch
            {
                "layout" => $"_ = typeof({directive.Rest});",
                "page" when directive.Rest.StartsWith('"') => $"_ = {directive.Rest};",
                "rendermode" => $"_ = ({directive.Rest});",
                _ => null,
            } is { } code)
            {
                events.Add(Region(directive.Start, code));
            }
        }

        var regions = Scoped(events, text.Length);
        var members = Members(text, razor, className, typeParameters);
        var embedded = new List<FileExtract>();

        // A component is compiled into the namespace its directory's path gives it even where its markup declares no
        // code of its own, so that namespace is declared - what a use of its whole name from another file walks.
        if (members.Count > 0 || regions.Count > 0 || (component && scope is not null))
        {
            // A view whose code declares no member of its class is the section alone: the class its markup's code is read
            // in is folded into it.
            var alone = !component && members.Count == 0;
            var opened = $"{(scope is null || alone ? string.Empty : $"namespace {scope}; ")}partial class {Head(className, typeParameters)} {{ ";
            var read = EmbeddedCode.CSharp(Language, input.Path, lines, lineCount, opened, members, regions, component ? new ExtractQualifier(CodeQualifierKinds.This) : null);

            // Where the class the code is read as is the component the file is, the class is the node, the one made for it
            // before is not: one type, the component, its members its own - named as its tags name it, a generic one too.
            if (component && read.Nodes.Any(node => node.Id == head.Id && node.Kind == CodeNodeKinds.Type))
            {
                read = read with { Nodes = [.. read.Nodes.Select(node => node.Id == head.Id ? node with { SubKind = "component", Name = className } : node)] };
                head = null;
            }
            else if (alone)
            {
                read = EmbeddedCode.Folded(read, node => node.Kind == CodeNodeKinds.Type && node.Parent is null ? from : null);
            }

            embedded.Add(read);
        }

        if (Usings(text, lines, razor, Imports.Contains(name)) is { } usings)
        {
            var read = CSharpExtractor.Read(Language, new CodeFileInput(input.Path, usings));

            // An _Imports.razor's @namespace is the namespace of every component under it that names none of its own, as
            // well as a scope its files see.
            var namespaces = name.Equals("_Imports.razor", StringComparison.OrdinalIgnoreCase)
                ? razor.Directives.Where(directive => directive.Name == "namespace" && directive.Rest.Length > 0).Select(directive => FileExtract.LineWithin(lines.LineAt(directive.Start), lineCount)).ToHashSet()
                : [];

            embedded.Add(Imports.Contains(name)
                ? read with { Imports = [.. read.Imports.Select(import => import.Form == CodeImportForms.Scope ? import with { Form = CodeImportForms.Directory, Namespace = namespaces.Contains(import.Line) } : import)] }
                : read);
        }

        // The partials its code renders and what its elements say, in the order the file writes them. A component that names
        // no namespace of its own is in the one its directory's path gives until a directory above it names one.
        return EmbeddedCode.Merged(new FileExtract(head is null ? [] : [head]) { References = [.. references.OrderBy(reference => reference.Line)] }, embedded)
            with { FolderScope = component && ownNamespace is null ? scope ?? string.Empty : null };

        Event Region(long at, string code) => new(at, EventKind.Region, 0, [], new CodeRegion(from, string.Empty, at, code));

        // Each partial view a call of the code renders by its name - Html.Partial("x") and its kin - an instantiates of it.
        void Partials(SyntaxNode code, long offset)
        {
            foreach (var call in code.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
            {
                if (call.Expression is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "Html" }, Name.Identifier.ValueText: var method }
                    && PartialMethods.Contains(method)
                    && call.ArgumentList.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression)
                    && literal.Token.ValueText is { Length: > 0 } partial)
                {
                    references.Add(new ExtractReference(from, CodeEdgeKinds.Instantiates, ExtractText.Whole(partial), FileExtract.LineWithin(lines.LineAt(Math.Max(offset + literal.SpanStart, 0)), lineCount)));
                }
            }
        }
    }

    /// <summary>Whether <paramref name="value"/> names a method - <c>Pay</c>, <c>Cart.Add</c>, <c>this.Pay</c> - and does nothing else.</summary>
    private static bool IsMethodGroup(ExpressionSyntax value)
    {
        var at = value;

        while (at is MemberAccessExpressionSyntax access && access.IsKind(SyntaxKind.SimpleMemberAccessExpression))
        {
            at = access.Expression;
        }

        return !value.ContainsDiagnostics && (at is IdentifierNameSyntax or GenericNameSyntax || (at is ThisExpressionSyntax && value != at));
    }

    /// <summary>A name the markup declares by itself, written as a parameter of no type the file says.</summary>
    private static (string Name, string Written) Unnamed(string name) => (name, $"var {name}");

    /// <summary>
    /// The regions the markup's code is read in, each given the names declared where it stands that it names, by one walk
    /// over <paramref name="events"/> in the order the file writes them (<see cref="DeclaredNames"/>): a component's
    /// content and a section each a stretch of their own; a name a statement's own block declares - a <c>@{ }</c>
    /// block's local, a pattern variable of a statement at the top - declared to the end of the stretch it stands in, the
    /// file's where it stands in none, as Razor compiles each into the method or the template that holds it.
    /// </summary>
    private static List<CodeRegion> Scoped(List<Event> events, long length)
    {
        var declared = new DeclaredNames();
        var holders = new Stack<long>();
        var regions = new List<CodeRegion>();

        foreach (var held in events.OrderBy(held => held.At).ThenBy(held => held.Kind))
        {
            declared.Leave(held.At);

            while (holders.Count > 0 && holders.Peek() <= held.At)
            {
                holders.Pop();
            }

            switch (held.Kind)
            {
                case EventKind.Holder:
                    holders.Push(held.End);
                    declared.Declare(held.Names, held.End);
                    break;

                case EventKind.Declaration:
                    declared.Declare(held.Names, held.End >= 0 ? held.End : holders.Count > 0 ? holders.Peek() : length);
                    break;

                case EventKind.Region when held.Region is { } region:
                    regions.Add(region with { Parameters = declared.Given(region.Code) });
                    break;

                default:
                    break;
            }
        }

        return regions;
    }

    /// <summary>
    /// The C# of <paramref name="construct"/> as Razor compiles it, standing where the file writes it and every other
    /// character blank but a line feed: its statements' words, parentheses, blocks and code - a <c>@{ }</c>'s braces
    /// none, for they open no scope - and, in a component, a block over the content of each component its markup holds,
    /// which is a template of its own: <paramref name="elements"/> read from <paramref name="next"/>, left past the
    /// construct's, for the constructs stand apart in the order the file writes them.
    /// </summary>
    private static string Code(CodeText text, Construct construct, IReadOnlyList<MarkupElement> elements, ref int next)
    {
        var code = new char[construct.End - construct.Start];

        for (var at = 0; at < code.Length; at++)
        {
            code[at] = text[construct.Start + at] == '\n' ? '\n' : ' ';
        }

        foreach (var (start, end) in construct.CSharp)
        {
            text.CopyTo(start, code.AsSpan((int)(start - construct.Start), (int)(end - start)));
        }

        for (; next < elements.Count && elements[next].Start < construct.End; next++)
        {
            var element = elements[next];

            if (element.Start > construct.Start && element.End <= construct.End && element.End > element.Close && element.Close > element.OpenEnd && MarkupDialect.Razor.Instantiated(element))
            {
                code[element.OpenEnd - 1 - construct.Start] = '{';
                code[element.Close - construct.Start] = '}';
            }
        }

        return new string(code);
    }

    /// <summary>
    /// The class's members as the file writes them, each where it stands: each code block's code without its own braces;
    /// an <c>@inject</c> a property of the type it is given, <c>@model</c> the <c>Model</c> property, and
    /// <c>@inherits</c>, <c>@implements</c> and <c>@attribute</c> a part of the class with that base or attribute.
    /// </summary>
    private static List<(long Offset, string Code)> Members(CodeText text, Syntax razor, string className, List<Match> typeParameters)
    {
        var members = new List<(long Offset, string Code)>();
        var named = Head(className, typeParameters);

        foreach (var (_, open, close) in razor.Blocks)
        {
            members.Add((open + 1, text.Slice(open + 1, Math.Max(open + 1, close))));
        }

        foreach (var directive in razor.Directives)
        {
            var written = directive.Rest.TrimEnd(';').Trim();

            if (directive.Name switch
            {
                "inject" when Injected.Match(written) is { Success: true } injected => $"{injected.Groups["type"].Value} {injected.Groups["name"].Value} {{ get; }}",
                "model" when written.Length > 0 => $"{written} Model {{ get; }}",
                "inherits" or "implements" when written.Length > 0 => $"}} partial class {named} : {written} {{",
                "attribute" when written.Length > 0 => $"}} {written} partial class {named} {{",
                _ => null,
            } is { } member)
            {
                members.Add((directive.Start, member));
            }
        }

        members.Sort((one, other) => one.Offset.CompareTo(other.Offset));
        return members;
    }

    /// <summary>The class's name as its declaration writes it: its type parameters, each <c>@typeparam</c>'s, and their constraints.</summary>
    private static string Head(string className, List<Match> typeParameters)
        => typeParameters.Count == 0
            ? className
            : $"{className}<{string.Join(", ", typeParameters.Select(parameter => parameter.Groups["name"].Value))}> {string.Join(' ', typeParameters.Select(parameter => parameter.Groups["where"].Value.Trim()).Where(where => where.Length > 0))}";

    /// <summary>
    /// The names the C# of a construct declares - a variable's, a loop's, a pattern's, a catch's - each with how a
    /// parameter of it is written (its type where the code says one, as the C# reader reads a local's), where it is
    /// declared and where what it is declared in ends within <paramref name="node"/>'s text, <see langword="null"/> where
    /// that is <paramref name="node"/>'s own block, which opens no scope: a lambda's and a local function's own are none.
    /// </summary>
    private static IEnumerable<(string Name, string Written, int From, int? To)> Names(SyntaxNode node)
    {
        foreach (var held in node.DescendantNodesAndSelf(child => child is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)))
        {
            var (token, type, scope) = held switch
            {
                VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } variable
                    => (variable.Identifier, declaration.Type.IsVar ? CSharpExtractor.Inferred(variable.Initializer?.Value) : declaration.Type, declaration.Parent is LocalDeclarationStatementSyntax local ? local.Parent : declaration.Parent),
                ForEachStatementSyntax loop => (loop.Identifier, loop.Type, loop),
                SingleVariableDesignationSyntax designation => (designation.Identifier, designation.Parent switch
                {
                    DeclarationExpressionSyntax declared => declared.Type,
                    DeclarationPatternSyntax declared => declared.Type,
                    RecursivePatternSyntax declared => declared.Type,
                    _ => null,
                }, designation.FirstAncestorOrSelf<StatementSyntax>()?.Parent),
                CatchDeclarationSyntax caught => (caught.Identifier, caught.Type, caught.Parent),
                _ => (default(SyntaxToken), (TypeSyntax?)null, (SyntaxNode?)null),
            };

            if (token.ValueText is { Length: > 0 } name)
            {
                var written = type is null || type.IsVar ? $"var {name}" : $"{type.WithoutTrivia()} {name}";
                yield return (name, written, token.SpanStart, scope is null || scope == node ? null : scope.Span.End);
            }
        }
    }

    /// <summary>
    /// Each <c>@using</c> as the C# directive it is, on the line it stands on, and nothing else - and, in a file whose
    /// directives reach its directory's every file (<paramref name="imports"/>), its <c>@namespace</c> as one too: the
    /// namespace every component under it is in, and so sees the names of; <see langword="null"/> where the file holds none.
    /// </summary>
    private static CodeText? Usings(CodeText text, CodeLineIndex lines, Syntax razor, bool imports)
    {
        var usings = razor.Directives.Where(directive => (directive.Name == "using" || (imports && directive.Name == "namespace")) && directive.Rest.Length > 0).ToList();

        if (usings.Count == 0)
        {
            return null;
        }

        var written = new EmbeddedText(text, lines);

        foreach (var directive in usings)
        {
            written.Write(directive.Start, $"using {directive.Rest.TrimEnd(';').Trim()};");
        }

        return written.Text();
    }

    /// <summary>
    /// The namespace a component with no <c>@namespace</c> is given: its directory's path from the tree's root, each part
    /// made a C# name as Razor makes one; <see langword="null"/> at the root, where it is in none.
    /// </summary>
    private static string? FolderNamespace(string path)
        => path.LastIndexOf('/') is var slash and > 0 ? string.Join('.', path[..slash].Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Identifier)) : null;

    /// <summary>
    /// <paramref name="name"/> made a C# name as Razor makes a file's or a folder's one: each character no name may hold an
    /// underscore, and an underscore before a first one that may not begin a name - <c>my-app</c> is <c>my_app</c>.
    /// </summary>
    private static string Identifier(string name)
    {
        if (name.Length == 0)
        {
            return "_";
        }

        var built = new StringBuilder(name.Length + 1);

        if (!SyntaxFacts.IsIdentifierStartCharacter(name[0]))
        {
            built.Append('_');
        }

        foreach (var character in name)
        {
            built.Append(SyntaxFacts.IsIdentifierPartCharacter(character) ? character : '_');
        }

        return built.ToString();
    }

    /// <summary>What the walk that gives each region its names meets, in the order it is met where two stand at one place.</summary>
    private enum EventKind
    {
        /// <summary>A stretch of the markup that holds code of its own: a component's content, a section.</summary>
        Holder,

        /// <summary>A name the markup's code declares.</summary>
        Declaration,

        /// <summary>A region of code.</summary>
        Region,
    }

    /// <summary>One thing the walk meets: where, what, where what it declares ends - -1 for the end of the stretch it stands in - its names, and its region.</summary>
    private sealed record Event(long At, EventKind Kind, long End, IReadOnlyList<(string Name, string Written)> Names, CodeRegion? Region);

    /// <summary>A statement Razor's markup writes at its top - an <c>@{ }</c> block, an <c>@if</c> and its kin - from its <c>@</c> to its end.</summary>
    private sealed class Construct(long start)
    {
        /// <summary>Where its <c>@</c> stands.</summary>
        public long Start { get; } = start;

        /// <summary>Where it ends: past its last block.</summary>
        public long End { get; set; }

        /// <summary>The ranges of it that are C# as written: the rest its markup, and the expressions its markup holds.</summary>
        public List<(long Start, long End)> CSharp { get; } = [];
    }

    /// <summary>A directive a line of Razor begins with.</summary>
    /// <param name="Name">Its name: <c>using</c>, <c>inject</c>.</param>
    /// <param name="Rest">What it says, the rest of its line.</param>
    /// <param name="Start">Where its <c>@</c> stands.</param>
    /// <param name="End">Where its line ends.</param>
    private sealed record Directive(string Name, string Rest, long Start, long End);

    /// <summary>
    /// What a Razor file holds, as Razor's own parser reads it from the top, in one walk over its text with a stack of what
    /// is open - the markup, the code, the markup that code switches to - so no nesting is too deep to read: its comments
    /// (<c>@* *@</c>); its class's code blocks (<c>@code</c>, <c>@functions</c>, the brace on their line or a later one);
    /// the directives a line begins with; each statement it writes - an <c>@{ }</c> block, an <c>@if</c>, <c>@foreach</c>,
    /// <c>@try</c> and their kin, with their <c>else</c>, <c>else if</c>, <c>catch</c>, <c>finally</c> and a <c>do</c>'s
    /// <c>while</c> - its C# read by C#'s own lexer, and the markup Razor switches to inside it - an element where a
    /// statement may begin, <c>&lt;text&gt;</c>, an <c>@:</c> line, an <c>@&lt;tag&gt;</c> template - each held to the end
    /// tag that closes it, the elements of its tag it holds counted; each expression - <c>@x.y(z)</c>, <c>@await x</c>,
    /// <c>@( )</c> - in a text or an attribute's value, but a directive attribute's, whose value is C# of its own, and an
    /// address's <c>@</c> and an <c>@@</c>, which are text; and each <c>@section</c>'s markup. What is no markup - each
    /// range of it - is what the markup reader is not given.
    /// </summary>
    private sealed class Syntax
    {
        /// <summary>The characters short of which the markup holds nothing the walk reads but text.</summary>
        private static readonly SearchValues<char> Special = SearchValues.Create("@<\n{}");

        private readonly CodeText _text;
        private readonly Stack<Frame> _frames = new();

        /// <summary>The statement at the markup's top whose code the walk is in; <see langword="null"/> where it is in none.</summary>
        private Construct? _construct;

        private Syntax(CodeText text) => _text = text;

        /// <summary>The comments.</summary>
        public List<(long Start, long End)> Comments { get; } = [];

        /// <summary>The class's code blocks: where each one's word, its opening brace and its closing brace stand.</summary>
        public List<(long Keyword, long Open, long Close)> Blocks { get; } = [];

        /// <summary>The directives.</summary>
        public List<Directive> Directives { get; } = [];

        /// <summary>The statements at the markup's top, each with the C# it holds - that of the markup it switches to among it.</summary>
        public List<Construct> Constructs { get; } = [];

        /// <summary>The expressions: where each one's C# begins and ends.</summary>
        public List<(long Start, long End)> Expressions { get; } = [];

        /// <summary>The sections' markup: where each begins and ends.</summary>
        public List<(long Start, long End)> Sections { get; } = [];

        /// <summary>What is no markup: the ranges the markup reader is not given.</summary>
        public List<(long Start, long End)> Code { get; } = [];

        private long Length => _text.Length;

        /// <summary>What <paramref name="text"/> holds.</summary>
        public static Syntax Of(CodeText text)
        {
            var syntax = new Syntax(text);
            syntax.Walk();
            return syntax;
        }

        private void Walk()
        {
            _frames.Push(new Frame { Root = true });

            for (var at = 0L; at < Length;)
            {
                var frame = _frames.Peek();
                at = frame.Code ? InCode(frame, at) : InMarkup(frame, at);
            }

            // What is open at the end of the text ends with it.
            foreach (var frame in _frames.Where(frame => frame.Code))
            {
                Run(frame, Length);
            }

            if (_construct is not null)
            {
                _construct.End = Length;
            }
        }

        /// <summary>One step of the walk in markup at <paramref name="at"/>: answers where the next begins.</summary>
        private long InMarkup(Frame frame, long at)
        {
            var character = _text[at];

            if (frame.Line && character == '\n')
            {
                Pop(at);
                return at;
            }

            // A comment's text and a raw element's - a script's, a title's - hold no tag, but code they may.
            if (at < frame.TextUntil)
            {
                return character == '@' ? Transition(frame, at, inAttribute: false) : at + 1;
            }

            switch (character)
            {
                case '@':
                    return Transition(frame, at, inAttribute: false);

                case '<':
                    return Tag(frame, at);

                case '{' when frame.Section:
                    frame.Braces++;
                    return at + 1;

                case '}' when frame.Section:
                    if (frame.Braces > 0)
                    {
                        frame.Braces--;
                        return at + 1;
                    }

                    Sections.Add((frame.Start, at));
                    NotMarkup(at, at + 1);
                    _frames.Pop();
                    return at + 1;

                default:
                    return at + 1 < Length && _text.FindAny(Special, at + 1, Length) is var next and >= 0 ? next : Length;
            }
        }

        /// <summary>An <c>@</c> in markup at <paramref name="at"/>: what Razor reads it as - in an attribute's value, an expression's alone.</summary>
        private long Transition(Frame frame, long at, bool inAttribute)
        {
            if (at + 1 >= Length)
            {
                return at + 1;
            }

            var next = _text[at + 1];

            // @@ writes an @, and one an address is written with - a letter or a digit on each side of it - is text.
            if (next == '@')
            {
                return at + 2;
            }

            if (next == '*')
            {
                return Comment(at);
            }

            if (at > 0 && char.IsLetterOrDigit(_text[at - 1]) && (char.IsLetterOrDigit(next) || next == '_'))
            {
                return at + 1;
            }

            if (next == '(')
            {
                var close = CSharpExtractor.Closing(_text, at + 1);
                Expressions.Add((at + 2, close));
                NotMarkup(at, Math.Min(close + 1, Length));
                return Math.Min(close + 1, Length);
            }

            if (!inAttribute && next == '{')
            {
                var owns = Begin(at);
                NotMarkup(at, at + 2);
                _frames.Push(new Frame { Code = true, Razor = true, RunStart = at + 2, Owns = owns });
                return at + 2;
            }

            if (!SyntaxFacts.IsIdentifierStartCharacter(next))
            {
                return at + 1;
            }

            var word = Word(at + 1);
            var after = at + 1 + word.Length;

            if (!inAttribute && frame.Root && _construct is null && StartsLine(at))
            {
                if (word is "code" or "functions" && Skipped(after) is var open && open < Length && _text[open] == '{')
                {
                    var close = CSharpExtractor.Closing(_text, open);
                    Blocks.Add((at, open, close));
                    NotMarkup(at, Math.Min(close + 1, Length));
                    return Math.Min(close + 1, Length);
                }

                if (DirectiveNames.Contains(word) && (after == Length || _text[after] is ' ' or '\t' or '\n') && LineEnd(at) is var end
                    && _text.Slice(after, end).Trim() is var rest && (word != "using" || !rest.StartsWith('(')))
                {
                    Directives.Add(new Directive(word, rest, at, end));
                    NotMarkup(at, end);
                    return end;
                }

                // A section's markup is held to the brace that closes it.
                if (word == "section" && Skipped(after) is var named && Word(named) is { Length: > 0 } section && Skipped(named + section.Length) is var brace && brace < Length && _text[brace] == '{')
                {
                    NotMarkup(at, brace + 1);
                    _frames.Push(new Frame { Section = true, Start = brace + 1 });
                    return brace + 1;
                }
            }

            if (!inAttribute && StatementWords.Contains(word))
            {
                return Statement(at, word, after);
            }

            // An implicit expression: a name, and what Razor reads after it with no space - members, calls, indexes.
            var start = at + 1;
            var past = word == "await" && after < Length && _text[after] == ' ' && Skipped(after) is var awaited && awaited < Length && SyntaxFacts.IsIdentifierStartCharacter(_text[awaited])
                ? Implicit(awaited)
                : Implicit(start);

            Expressions.Add((start, past));
            NotMarkup(at, past);
            return past;
        }

        /// <summary>Where the implicit expression whose first name begins at <paramref name="start"/> ends.</summary>
        private long Implicit(long start)
        {
            var end = start + Word(start).Length;

            while (end < Length)
            {
                var character = _text[end];

                if (character is '(' or '[')
                {
                    end = Math.Min(CSharpExtractor.Closing(_text, end) + 1, Length);
                }
                else if (character == '.' && end + 1 < Length && SyntaxFacts.IsIdentifierStartCharacter(_text[end + 1]))
                {
                    end += 1 + Word(end + 1).Length;
                }
                else if (character == '?' && end + 2 < Length && _text[end + 1] == '.' && SyntaxFacts.IsIdentifierStartCharacter(_text[end + 2]))
                {
                    end += 2 + Word(end + 2).Length;
                }
                else if (character == '?' && end + 1 < Length && _text[end + 1] == '[')
                {
                    end = Math.Min(CSharpExtractor.Closing(_text, end + 1) + 1, Length);
                }
                else
                {
                    break;
                }
            }

            return end;
        }

        /// <summary>A statement at <paramref name="at"/>, its word ending at <paramref name="after"/>: its parenthesis, where it has one, and its block.</summary>
        private long Statement(long at, string word, long after)
        {
            var owns = Begin(at);
            NotMarkup(at, at + 1);
            var past = after;

            if (word is not ("do" or "try"))
            {
                var open = Skipped(after);

                if (open >= Length || _text[open] != '(')
                {
                    CSharp(at + 1, after);
                    return Ended(owns, after);
                }

                past = Math.Min(CSharpExtractor.Closing(_text, open) + 1, Length);
            }

            CSharp(at + 1, past);
            return Body(past, word switch { "if" => "if", "try" => "try", "do" => "do", _ => null }, owns);
        }

        /// <summary>The block a statement's head ends before at <paramref name="at"/>, its code read in its turn; where none follows, the statement's end.</summary>
        private long Body(long at, string? continued, bool owns)
        {
            var brace = Skipped(at);

            if (brace >= Length || _text[brace] != '{')
            {
                return Ended(owns, at);
            }

            _frames.Push(new Frame { Code = true, RunStart = brace, Statement = continued, Owns = owns });
            return brace + 1;
        }

        /// <summary>
        /// One run of the walk in C# from <paramref name="at"/>, token by token as C#'s own lexer reads it: to the markup a
        /// statement switches to, a comment of Razor's, or the brace that closes the block; answers where the walk goes on.
        /// </summary>
        private long InCode(Frame frame, long at)
        {
            foreach (var (kind, start, _) in CSharpExtractor.TokensFrom(_text, at))
            {
                if (kind == SyntaxKind.EndOfFileToken)
                {
                    break;
                }


                if (_text[start] == '@' && start + 1 < Length)
                {
                    switch (_text[start + 1])
                    {
                        case '*':
                            Run(frame, start);
                            var end = Comment(start);
                            frame.RunStart = end;
                            return end;

                        case ':' when frame.StatementStart:
                            Run(frame, start);
                            NotMarkup(start, start + 2);
                            _frames.Push(new Frame { Line = true });
                            return start + 2;

                        case '<' when start + 2 < Length && char.IsLetter(_text[start + 2]):
                            Run(frame, start);
                            NotMarkup(start, start + 1);
                            _frames.Push(new Frame { Tag = TagName(start + 2) });
                            return start + 1;

                        // A block of Razor's in code is its code where it stands, its braces no scope.
                        case '{':
                            Run(frame, start);
                            NotMarkup(start, start + 2);
                            _frames.Push(new Frame { Code = true, Razor = true, RunStart = start + 2 });
                            return start + 2;

                        // A statement written with an @ in code is the statement: the @ Razor's, the word C#'s.
                        case var _ when frame.StatementStart && StatementWords.Contains(Word(start + 1)):
                            Run(frame, start);
                            NotMarkup(start, start + 1);
                            frame.RunStart = start + 1;
                            return start + 1;

                        default:
                            break;
                    }
                }

                // An element where a statement may begin is markup, to the end tag that closes it.
                if (_text[start] == '<' && frame.StatementStart && frame.Parens == 0 && start + 1 < Length && char.IsLetter(_text[start + 1]))
                {
                    Run(frame, start);
                    _frames.Push(new Frame { Tag = TagName(start + 1) });
                    return start;
                }

                switch (kind)
                {
                    case SyntaxKind.OpenBraceToken:
                        frame.Braces++;
                        frame.StatementStart = true;
                        break;

                    case SyntaxKind.CloseBraceToken when frame.Braces == 0:
                        return Closed(frame, start);

                    case SyntaxKind.CloseBraceToken:
                        frame.Braces--;
                        frame.StatementStart = true;
                        break;

                    case SyntaxKind.SemicolonToken or SyntaxKind.ColonToken:
                        frame.StatementStart = frame.Parens == 0;
                        break;

                    case SyntaxKind.OpenParenToken or SyntaxKind.OpenBracketToken:
                        frame.Parens++;
                        frame.StatementStart = false;
                        break;

                    case SyntaxKind.CloseParenToken or SyntaxKind.CloseBracketToken:
                        frame.Parens = Math.Max(frame.Parens - 1, 0);
                        frame.StatementStart = false;
                        break;

                    case SyntaxKind.ElseKeyword or SyntaxKind.DoKeyword:
                        frame.StatementStart = true;
                        break;

                    default:
                        frame.StatementStart = false;
                        break;
                }
            }

            Run(frame, Length);
            return Length;
        }

        /// <summary>The brace at <paramref name="close"/> that closes a block of code: a statement's goes on where its words say - <c>else</c>, <c>catch</c>, <c>finally</c>, a <c>do</c>'s <c>while</c>.</summary>
        private long Closed(Frame frame, long close)
        {
            _frames.Pop();

            if (frame.Razor)
            {
                Run(frame, close);
                NotMarkup(close, close + 1);
                Resume(close + 1);
                return Ended(frame.Owns, close + 1);
            }

            Run(frame, close + 1);
            var at = Skipped(close + 1);
            var word = Word(at);

            switch (frame.Statement, word)
            {
                case ("if", "else"):
                    var next = Skipped(at + word.Length);

                    if (Word(next) == "if" && Skipped(next + 2) is var open && open < Length && _text[open] == '(')
                    {
                        var past = Math.Min(CSharpExtractor.Closing(_text, open) + 1, Length);
                        CSharp(close + 1, past);
                        return Body(past, "if", frame.Owns);
                    }

                    CSharp(close + 1, at + word.Length);
                    return Body(at + word.Length, null, frame.Owns);

                case ("try", "catch"):
                    var caught = at + word.Length;

                    if (Skipped(caught) is var declared && declared < Length && _text[declared] == '(')
                    {
                        caught = Math.Min(CSharpExtractor.Closing(_text, declared) + 1, Length);
                    }

                    if (Skipped(caught) is var filter && Word(filter) == "when" && Skipped(filter + 4) is var condition && condition < Length && _text[condition] == '(')
                    {
                        caught = Math.Min(CSharpExtractor.Closing(_text, condition) + 1, Length);
                    }

                    CSharp(close + 1, caught);
                    return Body(caught, "try", frame.Owns);

                case ("try", "finally"):
                    CSharp(close + 1, at + word.Length);
                    return Body(at + word.Length, null, frame.Owns);

                case ("do", "while") when Skipped(at + word.Length) is var looped && looped < Length && _text[looped] == '(':
                    var ends = Math.Min(CSharpExtractor.Closing(_text, looped) + 1, Length);

                    if (Skipped(ends) is var semicolon && semicolon < Length && _text[semicolon] == ';')
                    {
                        ends = semicolon + 1;
                    }

                    CSharp(close + 1, ends);
                    return Ended(frame.Owns, ends);

                default:
                    return Ended(frame.Owns, close + 1);
            }
        }

        /// <summary>A tag in markup at <paramref name="at"/>, an end tag among them: the markup code switched to ends at the end tag that closes it.</summary>
        private long Tag(Frame frame, long at)
        {
            if (_text.At(at, "<!--"))
            {
                var end = _text.IndexOf("-->", at + 4);
                frame.TextUntil = end < 0 ? Length : end;
                return at + 4;
            }

            if (at + 1 < Length && _text[at + 1] == '/')
            {
                var name = TagName(at + 2);
                var past = _text.IndexOf('>', at) is var greater and >= 0 ? greater + 1 : Length;

                if (frame.Tag is not null && string.Equals(name, frame.Tag, StringComparison.OrdinalIgnoreCase) && --frame.Depth == 0)
                {
                    // <text> is Razor's, no markup of the page.
                    if (frame.Tag == "text")
                    {
                        NotMarkup(at, past);
                    }

                    Pop(past);
                }

                return past;
            }

            if (at + 1 >= Length || !char.IsLetter(_text[at + 1]))
            {
                return at + 1;
            }

            var tag = TagName(at + 1);
            var (closed, selfClosing) = Attributes(frame, at + 1 + tag.Length);

            if (frame.Tag is not null && string.Equals(tag, frame.Tag, StringComparison.OrdinalIgnoreCase))
            {
                if (frame.Tag == "text" && frame.Depth == 0)
                {
                    NotMarkup(at, closed);
                }

                if (!selfClosing && !MarkupText.IsVoid(tag))
                {
                    frame.Depth++;
                }

                if (frame.Depth == 0)
                {
                    Pop(closed);
                    return closed;
                }
            }

            if (!selfClosing && MarkupText.IsRaw(tag))
            {
                frame.TextUntil = EndTagOf(tag, closed) is var ends and >= 0 ? ends : Length;
            }

            return closed;
        }

        /// <summary>The attributes of a start tag from <paramref name="at"/>: answers where it ends and whether <c>/&gt;</c> ends it.</summary>
        private (long End, bool SelfClosing) Attributes(Frame frame, long at)
        {
            while (at < Length)
            {
                at = Skipped(at);

                if (at >= Length)
                {
                    break;
                }

                var character = _text[at];

                if (character == '>')
                {
                    return (at + 1, false);
                }

                if (character == '/' && at + 1 < Length && _text[at + 1] == '>')
                {
                    return (at + 2, true);
                }

                if (character == '@' && at + 1 < Length && _text[at + 1] == '*')
                {
                    at = Comment(at);
                    continue;
                }

                // A directive attribute - @onclick, @bind - is named with its @; its value is C# of its own.
                var name = at;

                while (at < Length && !char.IsWhiteSpace(_text[at]) && _text[at] is not ('=' or '>') && !(_text[at] == '/' && at + 1 < Length && _text[at + 1] == '>'))
                {
                    at++;
                }

                if (at == name)
                {
                    at++;
                    continue;
                }

                var directive = _text[name] == '@';
                var equals = Skipped(at);

                if (equals >= Length || _text[equals] != '=')
                {
                    continue;
                }

                var value = Skipped(equals + 1);

                if (value < Length && _text[value] is '"' or '\'')
                {
                    var quote = _text[value];

                    for (at = value + 1; at < Length && _text[at] != quote;)
                    {
                        at = !directive && _text[at] == '@' ? Transition(frame, at, inAttribute: true) : at + 1;
                    }

                    at = Math.Min(at + 1, Length);
                }
                else
                {
                    for (at = value; at < Length && !char.IsWhiteSpace(_text[at]) && _text[at] != '>';)
                    {
                        at = !directive && _text[at] == '@' ? Transition(frame, at, inAttribute: true) : at + 1;
                    }
                }
            }

            return (Length, false);
        }

        /// <summary>A comment of Razor's at <paramref name="at"/>: answers where it ends.</summary>
        private long Comment(long at)
        {
            var end = _text.IndexOf("*@", at + 2) is var stop and >= 0 ? stop + 2 : Length;
            Comments.Add((at, end));
            NotMarkup(at, end);
            return end;
        }

        /// <summary>Begins a statement at <paramref name="at"/>: one at the markup's top where none is under way; answers whether it is.</summary>
        private bool Begin(long at)
        {
            if (_construct is not null)
            {
                return false;
            }

            _construct = new Construct(at);
            Constructs.Add(_construct);
            return true;
        }

        /// <summary>Ends the statement at the markup's top at <paramref name="at"/>, where <paramref name="owns"/>; answers where the walk goes on.</summary>
        private long Ended(bool owns, long at)
        {
            if (owns && _construct is not null)
            {
                _construct.End = at;
                _construct = null;
            }

            return at;
        }

        /// <summary>Ends the markup code switched to at <paramref name="at"/>: the code goes on there, where a statement may begin.</summary>
        private void Pop(long at)
        {
            _frames.Pop();
            Resume(at);
        }

        /// <summary>Goes on with the code under what ended at <paramref name="at"/>, where one is: there, where a statement may begin.</summary>
        private void Resume(long at)
        {
            if (_frames.TryPeek(out var below) && below.Code)
            {
                below.RunStart = at;
                below.StatementStart = true;
            }
        }

        /// <summary>The C# of <paramref name="frame"/> from where it last began to <paramref name="end"/>.</summary>
        private void Run(Frame frame, long end)
        {
            if (end > frame.RunStart)
            {
                CSharp(frame.RunStart, end);
            }

            frame.RunStart = Math.Max(frame.RunStart, end);
        }

        /// <summary>C# of the statement under way, from <paramref name="start"/> to <paramref name="end"/>.</summary>
        private void CSharp(long start, long end)
        {
            if (end > start)
            {
                _construct?.CSharp.Add((start, end));
                NotMarkup(start, end);
            }
        }

        /// <summary>Holds that the text from <paramref name="start"/> to <paramref name="end"/> is no markup.</summary>
        private void NotMarkup(long start, long end)
        {
            if (end > start)
            {
                Code.Add((start, end));
            }
        }

        /// <summary>The name written at <paramref name="at"/>, as C# writes one; empty where none is.</summary>
        private string Word(long at)
        {
            if (at >= Length || !SyntaxFacts.IsIdentifierStartCharacter(_text[at]))
            {
                return string.Empty;
            }

            var end = at + 1;

            while (end < Length && SyntaxFacts.IsIdentifierPartCharacter(_text[end]))
            {
                end++;
            }

            return _text.Slice(at, end);
        }

        /// <summary>The tag's name written at <paramref name="at"/>: what stands to a space, a <c>/</c> or a <c>&gt;</c>.</summary>
        private string TagName(long at)
        {
            var end = at;

            while (end < Length && !char.IsWhiteSpace(_text[end]) && _text[end] is not ('/' or '>'))
            {
                end++;
            }

            return _text.Slice(at, end);
        }

        /// <summary>Where the first character from <paramref name="at"/> that is no white space stands, line breaks among it.</summary>
        private long Skipped(long at)
        {
            while (at < Length && char.IsWhiteSpace(_text[at]))
            {
                at++;
            }

            return at;
        }

        /// <summary>Where the end tag of <paramref name="tag"/> - <c>&lt;/</c> and its name, in any case - first stands from <paramref name="from"/> on; -1 where none does.</summary>
        private long EndTagOf(string tag, long from)
        {
            var written = $"</{tag}";

            for (var at = from < Length ? _text.Find('<', from, Length) : -1; at >= 0; at = at + 1 < Length ? _text.Find('<', at + 1, Length) : -1)
            {
                if (_text.At(at, written, StringComparison.OrdinalIgnoreCase))
                {
                    return at;
                }
            }

            return -1;
        }

        /// <summary>Where the line <paramref name="at"/> stands on ends.</summary>
        private long LineEnd(long at) => _text.IndexOf('\n', at) is var end and >= 0 ? end : Length;

        /// <summary>Whether nothing but white space is before <paramref name="at"/> on its line.</summary>
        private bool StartsLine(long at)
        {
            for (var before = at - 1; before >= 0 && _text[before] != '\n'; before--)
            {
                if (_text[before] is not (' ' or '\t'))
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>What is open where the walk stands: markup - the file's, a section's, what code switched to - or a block of code.</summary>
    private sealed class Frame
    {
        /// <summary>Whether it is code; markup otherwise.</summary>
        public bool Code { get; init; }

        /// <summary>Whether it is the file's markup, at its top.</summary>
        public bool Root { get; init; }

        /// <summary>The tag of the element code switched to markup at, whose end tag ends it.</summary>
        public string? Tag { get; init; }

        /// <summary>How many elements of <see cref="Tag"/> are open.</summary>
        public int Depth { get; set; }

        /// <summary>Whether it is an <c>@:</c> line, which its line's end ends.</summary>
        public bool Line { get; init; }

        /// <summary>Whether it is a section's markup, which the brace that closes it ends.</summary>
        public bool Section { get; init; }

        /// <summary>Where a section's markup begins.</summary>
        public long Start { get; init; }

        /// <summary>How many braces are open: a section's text's, a block's code's.</summary>
        public int Braces { get; set; }

        /// <summary>Where the text that holds no tag - a comment's, a raw element's - ends.</summary>
        public long TextUntil { get; set; }

        /// <summary>Whether it is a <c>@{ }</c> block, whose braces are no C#.</summary>
        public bool Razor { get; init; }

        /// <summary>Where its C# last began.</summary>
        public long RunStart { get; set; }

        /// <summary>The statement whose block it is, where words may go on with it: <c>if</c>, <c>try</c>, <c>do</c>.</summary>
        public string? Statement { get; init; }

        /// <summary>Whether its close ends the statement at the markup's top.</summary>
        public bool Owns { get; init; }

        /// <summary>How many parentheses and brackets are open.</summary>
        public int Parens { get; set; }

        /// <summary>Whether a statement may begin where the walk stands.</summary>
        public bool StatementStart { get; set; } = true;
    }

    /// <summary>An <c>@inject</c>'s rest: a type and the name it is given.</summary>
    private static readonly Regex Injected = LinearRegex.Compile(@"^(?<type>.+?)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)$");

    /// <summary>A <c>@typeparam</c>'s rest: the parameter's name and the constraints its <c>where</c> gives.</summary>
    private static readonly Regex TypeParameter = LinearRegex.Compile(@"^(?<name>[A-Za-z_][A-Za-z0-9_]*)(?<where>\s+where\s+.*)?$");
}
