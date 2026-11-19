using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// A Razor view (<c>.cshtml</c>) or a Blazor component (<c>.razor</c>). A component is a <c>type</c> named by its file in
/// its <c>@namespace</c> - else the namespace its directory's path gives, each part made a C# name as Razor makes it,
/// none at the tree's root - marked a part of its code-behind partial, which the linker joins to it; a view is a
/// <c>section</c> over its lines. The class either compiles to is read by the C# reader, with the markup blanked so each
/// declaration carries the line it stands on: the code of its <c>@code</c> and <c>@functions</c> blocks - each found as
/// C#'s own lexer reads it, so no brace a string or a comment holds ends one - an <c>@inject</c> a property of the type it
/// is given, <c>@model</c> its <c>Model</c> property, <c>@inherits</c> and <c>@implements</c> its bases; each
/// <c>@using</c> is read by the C# reader too, a scope import - a <c>_ViewImports.cshtml</c>'s and an
/// <c>_Imports.razor</c>'s reaching every file of the language under their directory. Its markup - comments, code and
/// directives taken out - is read by the markup reader every markup file shares (<see cref="MarkupText"/>,
/// <see cref="MarkupDialect.Razor"/>): a PascalCase or dotted element and a <c>&lt;partial name=</c> an
/// <c>instantiates</c>, as is an <c>Html.Partial("x")</c> and its kin; an <c>@on*</c> attribute a call of the
/// component's member it names, and <c>@bind</c> and another <c>@</c> attribute a use of one - but of a name the
/// markup's own C# declares where the attribute stands (<see cref="Declared"/>), which is no member. The C# is embedded
/// as every markup embeds code (<see cref="EmbeddedCode"/>).
/// </summary>
public sealed partial class RazorExtractor : ICodeGraphExtractor
{
    /// <summary>The names of the files whose <c>@using</c> reaches a whole directory's subtree.</summary>
    private static readonly HashSet<string> Imports = new(StringComparer.OrdinalIgnoreCase) { "_ViewImports.cshtml", "_Imports.razor" };

    /// <summary>The directives a line of Razor begins with: what it says of the file, never markup.</summary>
    private static readonly HashSet<string> DirectiveNames = new(StringComparer.Ordinal)
    {
        "using", "model", "inherits", "inject", "namespace", "layout", "attribute", "page", "implements", "typeparam", "preservewhitespace",
        "rendermode", "addTagHelper", "removeTagHelper", "tagHelperPrefix",
    };

    /// <summary>The statements whose parenthesis holds C#: what the markup reader is not given.</summary>
    private static readonly HashSet<string> StatementWords = new(StringComparer.Ordinal) { "if", "for", "foreach", "while", "switch", "lock", "using", "catch" };

    /// <inheritdoc/>
    public string Language => "razor";

    /// <inheritdoc/>
    public int Version => 1;

    /// <inheritdoc/>
    public CodeGraphClaims Claims { get; } = CodeGraphClaims.OfExtensions(".cshtml", ".razor");

    /// <inheritdoc/>
    public bool Outlines => true;

    /// <inheritdoc/>
    public CodeGraphExtractorHelp Help { get; } = new(
        "Razor views (.cshtml) and Blazor components (.razor): a component as a type named by its file in its @namespace - else the namespace its directory's path gives, none at the tree's root - a part of its code-behind partial, a view as a section over its lines; the class each compiles to read by the C# reader with the markup blanked - its @code and @functions blocks, an @inject a property of the type it is given, @model its Model property, @inherits and @implements its bases; @using read by the C# reader as a scope import - one a _ViewImports.cshtml or _Imports.razor holds reaching every file of the language under its directory; and its markup read by the reader every markup file shares: a PascalCase element, a partial name= and an Html.Partial(\"x\") as instantiates, an @on* attribute a call of the component's member it names, and @bind and another @ attribute a use of one - but of a name the markup's own C# declares where the attribute stands (an @foreach's, @for's, @using's or catch's variable and a pattern's over the statement's block, a @{ } block's local from where it is declared, a template's context - or the name its Context= gives - inside a component's content, an arrow function's parameter in its own value), which is the markup's and no member",
        [
            "inline C# in the markup - an @x, an @( ), a @{ } block - read as no code, and a component with no @namespace joins its code-behind only where its directory's path names the partial's namespace",
            "what a tag helper is, and an element no component of the tree names: an instantiates that resolves nowhere, counted as such",
            "a layout a view or component names by @layout, and the route an @page gives: no reference of them",
            "a generic component's @typeparam: its class read as no generic type",
        ]);

    /// <inheritdoc/>
    public FileExtract Extract(CodeFileInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var text = EmbeddedCode.LineFeeds(input.Text);
        var lineCount = input.Lines.Count;
        var name = CodeGraphIds.FileName(input.Path);
        var component = name.EndsWith(".razor", StringComparison.OrdinalIgnoreCase);
        var className = Identifier(name.LastIndexOf('.') is var dot and > 0 ? name[..dot] : name);
        var razor = Parts.Of(text);
        var scope = razor.Directives.FirstOrDefault(directive => directive.Name == "namespace" && directive.Rest.Length > 0)?.Rest ?? FolderNamespace(input.Path);
        var qualified = ExtractText.Qualify(scope, className);

        // The head of the extract, which the markup's references are made from: a component's type, a view's section.
        ExtractNode? head = component
            ? new ExtractNode($"{Language}:{qualified}", CodeNodeKinds.Type, className, qualified, 1, Math.Max(lineCount, 1), ExtractText.Signature(qualified)) { SubKind = "component", Part = true }
            : new ExtractNode($"{Language}:{input.Path}#view", CodeNodeKinds.Section, className, $"{input.Path}#view", 1, Math.Max(lineCount, 1), ExtractText.Signature(className)) { SubKind = "view" };
        var from = head.Id;

        var embedded = new List<FileExtract>();

        if (Members(text, razor, scope, className) is { } members)
        {
            var read = CSharpExtractor.Read(Language, new CodeFileInput(input.Path, members));

            // Where the class the code is read as is the component the file is, the class is the node, the one made for it
            // before is not: one type, the component, its members its own.
            if (component && read.Nodes.Any(node => node.Id == head.Id && node.Kind == CodeNodeKinds.Type))
            {
                read = read with { Nodes = [.. read.Nodes.Select(node => node.Id == head.Id ? node with { SubKind = "component" } : node)] };
                head = null;
            }

            embedded.Add(read);
        }

        if (Usings(text, razor) is { } usings)
        {
            var read = CSharpExtractor.Read(Language, new CodeFileInput(input.Path, usings));

            embedded.Add(Imports.Contains(name)
                ? read with { Imports = [.. read.Imports.Select(import => import.Form == CodeImportForms.Scope ? import with { Form = CodeImportForms.Directory } : import)] }
                : read);
        }

        // The markup with what is no markup taken out - Razor's comments, the code blocks, the directives - so it is read
        // where it stands; the C# its expressions hold taken out too for the markup reader, though not for the partials
        // they render.
        var references = new List<ExtractReference>();
        var outside = EmbeddedCode.Blanked(text, [.. razor.Comments, .. razor.Blocks.Select(block => (block.Keyword, Math.Min(block.Close + 1, text.Length))), .. razor.Directives.Select(directive => (directive.Start, directive.End))]);
        var lines = CodeFileText.LineIndex(outside);

        foreach (Match partial in HtmlPartials().Matches(outside))
        {
            references.Add(new ExtractReference(from, CodeEdgeKinds.Instantiates, ExtractText.Whole(partial.Groups["name"].Value), FileExtract.LineWithin(lines.LineAt(partial.Index), lineCount)));
        }

        var markupText = EmbeddedCode.Blanked(outside, razor.Expressions);
        var markup = MarkupText.Read(markupText, MarkupDialect.Razor.ClosesAsHtml);
        var qualifier = component ? new ExtractQualifier(CodeQualifierKinds.This) : null;
        var declared = Declared(text, outside, markupText, razor, markup);
        var contexts = new HashSet<string>?[markup.Elements.Count];

        for (var at = 0; at < markup.Elements.Count; at++)
        {
            var element = markup.Elements[at];
            var inherited = element.Parent >= 0 ? contexts[element.Parent] : null;

            // A template's context is declared for what a component holds - by the name its Context= gives, else context.
            contexts[at] = MarkupDialect.Razor.Instantiated(element)
                ? new HashSet<string>(inherited ?? [], StringComparer.Ordinal) { element.Attribute("context")?.Trim() is { Length: > 0 } named ? named : "context" }
                : inherited;

            var local = new HashSet<string>(inherited ?? [], StringComparer.Ordinal);
            local.UnionWith(declared.Where(name => name.From <= element.Start && element.Start < name.To).Select(name => name.Name));
            MarkupDialect.Razor.Read(element, from, qualifier, references, local.Count > 0 ? local : null);
        }

        // The partials its text renders and what its elements say, in the order the file writes them.
        return EmbeddedCode.Merged(new FileExtract(head is null ? [] : [head]) { References = [.. references.OrderBy(reference => reference.Line)] }, embedded);
    }

    /// <summary>
    /// The class the file compiles to as C# the C# reader reads, each part on the line it stands on and every other line
    /// blank: on the first line, before what it holds, the namespace and the class's opening; each code block's code
    /// without its own braces; an <c>@inject</c> a property of the type it is given, <c>@model</c> the <c>Model</c>
    /// property, <c>@inherits</c> and <c>@implements</c> a part of the class with that base; and the class's close after
    /// the last line. <see langword="null"/> where the file holds none of them.
    /// </summary>
    private static string? Members(string text, Parts razor, string? scope, string className)
    {
        var spans = CodeFileText.Spans(text);
        var code = new string[spans.Count];
        var any = false;

        foreach (var (_, open, close) in razor.Blocks)
        {
            for (var line = LineOf(spans, open); line < spans.Count && spans[line].Start < close; line++)
            {
                var start = Math.Max(spans[line].Start, open + 1);
                var end = Math.Min(spans[line].End, close);

                if (end > start)
                {
                    code[line] = code[line] is null ? text[start..end] : $"{code[line]} {text[start..end]}";
                }
            }

            any = true;
        }

        foreach (var directive in razor.Directives)
        {
            var written = directive.Rest.TrimEnd(';').Trim();

            code[directive.Line] = directive.Name switch
            {
                "inject" when Injected().Match(written) is { Success: true } injected => $"{injected.Groups["type"].Value} {injected.Groups["name"].Value} {{ get; }}",
                "model" when written.Length > 0 => $"{written} Model {{ get; }}",
                "inherits" or "implements" when written.Length > 0 => $"}} partial class {className} : {written} {{",
                _ => code[directive.Line],
            };

            any |= code[directive.Line] is not null && directive.Name is "inject" or "model" or "inherits" or "implements";
        }

        if (!any)
        {
            return null;
        }

        code[0] = $"{(scope is null ? string.Empty : $"namespace {scope}; ")}partial class {className} {{ {code[0]}";
        code[^1] = $"{code[^1]} }}";
        return string.Join('\n', code);
    }

    /// <summary>
    /// The names the markup's own C# declares, each with the stretch of the text it is declared over, as Razor compiles
    /// the markup into one method: a statement's - an <c>@foreach</c>'s item, an <c>@for</c>'s, an <c>@using</c>'s and a
    /// <c>catch</c>'s variables, a pattern's (<c>@if (x is Item item)</c>) - over the statement to its block's close; and
    /// a <c>@{ }</c> block's locals from where each is declared to the close of the block of markup that holds the
    /// <c>@{ }</c> - which opens no scope of its own - or the file's end, a statement inside it over that statement alone.
    /// Each block is matched by its braces in <paramref name="read"/>, the text the markup reader reads, passing over every
    /// start tag, whose attributes may hold braces of their own.
    /// </summary>
    /// <param name="text">The file's text.</param>
    /// <param name="outside">The text with its comments, its code blocks and its directives blanked.</param>
    /// <param name="read">The text the markup reader reads: <paramref name="outside"/> with the C# of its expressions blanked too.</param>
    /// <param name="razor">What the file holds that is no markup.</param>
    /// <param name="markup">The markup <paramref name="read"/> holds.</param>
    private static List<(string Name, int From, int To)> Declared(string text, string outside, string read, Parts razor, MarkupText markup)
    {
        var tags = markup.Elements.Select(element => (element.Start, End: Math.Max(element.OpenEnd, element.Start + 1))).OrderBy(tag => tag.Start).ToList();
        var closes = new Dictionary<int, int>();
        var blocks = new HashSet<int>();
        var open = new Stack<int>();
        var tag = 0;

        for (var at = 0; at < read.Length; at++)
        {
            while (tag < tags.Count && tags[tag].End <= at)
            {
                tag++;
            }

            if (tag < tags.Count && tags[tag].Start <= at)
            {
                at = tags[tag].End - 1;
                continue;
            }

            if (read[at] == '{')
            {
                open.Push(at);

                if (at > 0 && read[at - 1] == '@' && (at < 2 || read[at - 2] != '@'))
                {
                    blocks.Add(at);
                }
            }
            else if (read[at] == '}' && open.Count > 0)
            {
                closes[open.Pop()] = at;
            }
        }

        var declared = new List<(string Name, int From, int To)>();

        // The close of the block that holds the offset: the innermost pair of braces around it a @{ } opens none of.
        int Holding(int offset)
            => closes.Where(pair => pair.Key < offset && pair.Value > offset && !blocks.Contains(pair.Key)).Select(pair => (int?)pair.Value).Max() is { } close ? close : text.Length;

        foreach (var (word, parenthesis, close) in razor.Statements)
        {
            var body = Skipped(read, Math.Min(close + 1, read.Length));
            var to = body < read.Length && read[body] == '{' && closes.TryGetValue(body, out var end) ? end : Holding(parenthesis);
            var header = text[(parenthesis + 1)..Math.Min(close, text.Length)];
            var statement = SyntaxFactory.ParseStatement(word == "catch" ? $"try {{ }} catch ({header}) {{ }}" : $"{word} ({header}) {{ }}");

            declared.AddRange(Names(statement).Select(name => (name.Name, parenthesis, to)));
        }

        foreach (var brace in blocks)
        {
            var end = closes.TryGetValue(brace, out var closed) ? closed : read.Length;
            var code = new StringBuilder(outside[(brace + 1)..end]);

            // The markup the block holds is no C#: blanked, so the block is read as the statements around it.
            foreach (var element in markup.Elements.Where(element => element.Start > brace && element.Start < end))
            {
                for (var at = element.Start; at < Math.Min(element.End, end); at++)
                {
                    code[at - brace - 1] = ' ';
                }
            }

            var root = SyntaxFactory.ParseStatement($"{{{code}}}");
            var holding = Holding(brace);

            foreach (var (name, from, to) in Names(root))
            {
                declared.Add((name, brace + from, to is { } inner ? brace + inner : holding));
            }
        }

        return declared;
    }

    /// <summary>
    /// The names <paramref name="node"/> declares - a variable's, a loop's, a pattern's, a catch's - each with where it is
    /// declared and where what it is declared in ends within <paramref name="node"/>'s text, <see langword="null"/> where
    /// that is <paramref name="node"/>'s own block, which opens no scope: a lambda's and a local function's own are none.
    /// </summary>
    private static IEnumerable<(string Name, int From, int? To)> Names(SyntaxNode node)
    {
        foreach (var held in node.DescendantNodesAndSelf(child => child is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)))
        {
            var (token, scope) = held switch
            {
                VariableDeclaratorSyntax variable => (variable.Identifier, variable.Parent?.Parent is LocalDeclarationStatementSyntax ? variable.Parent.Parent.Parent : variable.Parent?.Parent),
                ForEachStatementSyntax loop => (loop.Identifier, loop),
                SingleVariableDesignationSyntax designation => (designation.Identifier, designation.FirstAncestorOrSelf<StatementSyntax>()?.Parent),
                CatchDeclarationSyntax caught => (caught.Identifier, caught.Parent),
                _ => (default(SyntaxToken), (SyntaxNode?)null),
            };

            if (token.ValueText is { Length: > 0 } name)
            {
                yield return (name, token.SpanStart, scope is null || scope == node ? null : scope.Span.End);
            }
        }
    }

    /// <summary>Each <c>@using</c> as the C# directive it is, on the line it stands on, and every other line blank; <see langword="null"/> where the file holds none.</summary>
    private static string? Usings(string text, Parts razor)
    {
        var usings = razor.Directives.Where(directive => directive.Name == "using" && directive.Rest.Length > 0).ToList();

        if (usings.Count == 0)
        {
            return null;
        }

        var code = new string[CodeFileText.Spans(text).Count];

        foreach (var directive in usings)
        {
            code[directive.Line] = $"using {directive.Rest.TrimEnd(';').Trim()};";
        }

        return string.Join('\n', code);
    }

    /// <summary>Where the first character past <paramref name="at"/> that is no white space stands, line breaks among it.</summary>
    private static int Skipped(string text, int at)
    {
        while (at < text.Length && char.IsWhiteSpace(text[at]))
        {
            at++;
        }

        return at;
    }

    /// <summary>The index of the line <paramref name="offset"/> stands on.</summary>
    private static int LineOf(IReadOnlyList<CodeLineSpan> spans, int offset)
    {
        var line = 0;

        while (line + 1 < spans.Count && spans[line + 1].Start <= offset)
        {
            line++;
        }

        return line;
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

    /// <summary>
    /// What a Razor file holds that is no markup, as Razor reads it from the top: its comments (<c>@* *@</c>), its code
    /// blocks (<c>@code</c> and <c>@functions</c>, the brace on their line or a later one), the directives a line begins
    /// with, and the C# of its explicit expressions and of its statements' parentheses - each block and parenthesis
    /// closed where C#'s own lexer says - each statement by its word and its parenthesis besides.
    /// </summary>
    private sealed record Parts(
        List<(int Start, int End)> Comments,
        List<(int Keyword, int Open, int Close)> Blocks,
        List<Directive> Directives,
        List<(int Start, int End)> Expressions,
        List<(string Word, int Open, int Close)> Statements)
    {
        /// <summary>The parts <paramref name="text"/> holds.</summary>
        public static Parts Of(string text)
        {
            var parts = new Parts([], [], [], [], []);
            var lines = CodeFileText.LineIndex(text);

            for (var at = 0; at < text.Length;)
            {
                if (text[at] != '@' || at + 1 >= text.Length)
                {
                    at++;
                    continue;
                }

                var next = text[at + 1];

                if (next == '@')
                {
                    at += 2;
                    continue;
                }

                if (next == '*')
                {
                    var end = text.IndexOf("*@", at + 2, StringComparison.Ordinal) is var stop and >= 0 ? stop + 2 : text.Length;
                    parts.Comments.Add((at, end));
                    at = end;
                    continue;
                }

                if (next == '(')
                {
                    var close = CSharpExtractor.Closing(text, at + 1);
                    parts.Expressions.Add((at, Math.Min(close + 1, text.Length)));
                    at = Math.Min(close + 1, text.Length);
                    continue;
                }

                var word = Word(text, at + 1);
                var after = at + 1 + word.Length;

                if (StartsLine(text, at) && word is "code" or "functions" && Skipped(text, after) is var open && open < text.Length && text[open] == '{')
                {
                    var close = CSharpExtractor.Closing(text, open);
                    parts.Blocks.Add((at, open, close));
                    at = Math.Min(close + 1, text.Length);
                    continue;
                }

                if (StartsLine(text, at) && DirectiveNames.Contains(word) && (after == text.Length || text[after] is ' ' or '\t' or '\n'))
                {
                    var end = text.IndexOf('\n', at) is var stop and >= 0 ? stop : text.Length;
                    var rest = text[after..end].Trim();

                    // A using with a parenthesis is a statement, whose parenthesis holds C#.
                    if (word != "using" || !rest.StartsWith('('))
                    {
                        parts.Directives.Add(new Directive(word, rest, lines.LineAt(at) - 1, at, end));
                        at = end;
                        continue;
                    }
                }

                if (StatementWords.Contains(word) && Skipped(text, after) is var parenthesis && parenthesis < text.Length && text[parenthesis] == '(')
                {
                    var close = CSharpExtractor.Closing(text, parenthesis);
                    parts.Expressions.Add((parenthesis, Math.Min(close + 1, text.Length)));
                    parts.Statements.Add((word, parenthesis, Math.Min(close, text.Length)));
                    at = Math.Min(close + 1, text.Length);
                    continue;
                }

                at++;
            }

            return parts;
        }

        /// <summary>The name written at <paramref name="at"/>: letters, digits and underscores.</summary>
        private static string Word(string text, int at)
        {
            var end = at;

            while (end < text.Length && (char.IsAsciiLetterOrDigit(text[end]) || text[end] == '_'))
            {
                end++;
            }

            return text[at..end];
        }


        /// <summary>Whether nothing but white space is before <paramref name="at"/> on its line.</summary>
        private static bool StartsLine(string text, int at)
        {
            for (var before = at - 1; before >= 0 && text[before] != '\n'; before--)
            {
                if (text[before] is not (' ' or '\t'))
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>A directive a line of Razor begins with.</summary>
    /// <param name="Name">Its name: <c>using</c>, <c>inject</c>.</param>
    /// <param name="Rest">What it says, the rest of its line.</param>
    /// <param name="Line">The index of its line, from 0.</param>
    /// <param name="Start">Where its <c>@</c> stands.</param>
    /// <param name="End">Where its line ends.</param>
    private sealed record Directive(string Name, string Rest, int Line, int Start, int End);

    /// <summary>An <c>@inject</c>'s rest: a type and the name it is given.</summary>
    [GeneratedRegex(@"^(?<type>.+?)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex Injected();

    /// <summary>A partial a view renders by its name: <c>Html.Partial("x")</c>, <c>PartialAsync</c>, <c>RenderPartial</c> and <c>RenderPartialAsync</c>, with what else they are given.</summary>
    [GeneratedRegex(@"\bHtml\.(?:Render)?Partial(?:Async)?\s*\(\s*""(?<name>[^""\n]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlPartials();
}
