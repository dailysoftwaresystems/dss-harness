using System.Globalization;
using System.Text.RegularExpressions;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// A Vue single-file component (<c>.vue</c>): the component a <c>type</c> SubKind <c>component</c> named by its file and
/// exported <c>default</c>, so <c>import Foo from './Foo.vue'</c> binds to it; its <c>&lt;script&gt;</c> or
/// <c>&lt;script setup&gt;</c> - TypeScript where <c>lang="ts"</c> - read by the JavaScript reader with the other lines
/// blanked, so its declarations carry the lines they stand on; its <c>&lt;template&gt;</c> read line by line as the
/// markup it is: a component's tag - <c>&lt;FooBar&gt;</c> or <c>&lt;foo-bar&gt;</c> - an <c>instantiates</c> of a
/// component of that name, a <c>v-on</c> or <c>@</c> attribute a call of the handler it names, a <c>v-bind</c>,
/// <c>:</c> attribute or <c>{{ }}</c> a use of the name its expression begins with, and each class and id a use of the
/// rule that styles it; its <c>&lt;style&gt;</c> - scoped among them - read by the style sheet's reader the same way,
/// its rules carrying their lines.
/// </summary>
public sealed partial class VueExtractor : ICodeGraphExtractor
{
    /// <inheritdoc/>
    public string Language => "vue";

    /// <inheritdoc/>
    public int Version => 2;

    /// <inheritdoc/>
    public CodeGraphClaims Claims { get; } = CodeGraphClaims.OfExtensions(".vue");

    /// <inheritdoc/>
    public bool Outlines => false;

    /// <inheritdoc/>
    public CodeGraphExtractorHelp Help { get; } = new(
        "Vue single-file components (.vue): the component a type named by its file and exported default, its script - TypeScript where lang=\"ts\" - read by the JavaScript reader with the other lines blanked, its template's component tags as instantiates, its v-on and @ attributes as calls, its v-bind, : and {{ }} as uses and its classes and ids as uses of the rules that style them, and its style block read by the style sheet's reader",
        [
            "a template's control flow - v-if, v-for, v-slot and the directives of the framework - as text, and an expression its markup holds past its first name: no reference of it",
            "a style block's SCSS, Less and its framework-specific syntaxes: read as the style sheet's reader reads them",
            "a component with no script block: the type alone, and nothing of its own members",
        ]);

    /// <inheritdoc/>
    public FileExtract Extract(CodeFileInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var lines = input.Lines;
        var nodes = new List<ExtractNode>();
        var references = new List<ExtractReference>();
        var imports = new List<ExtractImport>();
        var ids = new ExtractIds();
        var simple = CodeGraphIds.FileName(input.Path)[..^4];
        var code = new List<string>(lines.Count);
        var styles = new List<string>(lines.Count);

        for (var at = 0; at < lines.Count; at++)
        {
            code.Add(string.Empty);
            styles.Add(string.Empty);
        }

        var component = new ExtractNode(ids.Next($"{Language}:{input.Path}#{simple}"), CodeNodeKinds.Type, simple, $"{input.Path}#{simple}", 1, Math.Max(lines.Count, 1), simple)
        {
            SubKind = "component",
            Exports = ["default"],
        };
        nodes.Add(component);

        // The blocks the file holds: a script read by the JavaScript reader, a template read as markup, a style read by
        // the style sheet's - each whole lines of its own, the rest blank.
        var block = Block.None;
        var typescript = false;

        for (var at = 0; at < lines.Count; at++)
        {
            var line = lines[at];

            if (block == Block.None && Opening().Match(line) is { Success: true } opened)
            {
                block = opened.Groups["name"].Value switch
                {
                    "script" => Block.Script,
                    "template" => Block.Template,
                    "style" => Block.Style,
                    _ => Block.None,
                };
                typescript = block == Block.Script && line.Contains("lang=\"ts\"", StringComparison.Ordinal);

                if (block != Block.None && !line.Contains('>', StringComparison.Ordinal))
                {
                    continue;
                }

                if (block != Block.None && SelfClosing().IsMatch(line))
                {
                    block = Block.None;
                }

                continue;
            }

            if (Closing().IsMatch(line))
            {
                block = Block.None;
                continue;
            }

            switch (block)
            {
                case Block.Script:
                    code[at] = line;
                    break;

                case Block.Style:
                    styles[at] = line;
                    break;

                case Block.Template:
                    Template(component.Id, references, line, at + 1);
                    break;

                default:
                    break;
            }
        }

        var script = new JsReading(Language, input.Path, string.Join('\n', code), typescript, script: true).Read();
        nodes.AddRange(script.Nodes);
        references.AddRange(script.References);
        imports.AddRange(script.Imports);        var style = CssExtractor.Read(Language, new CodeFileInput(input.Path, string.Join('\n', styles)));
        nodes.AddRange(style.Nodes);
        references.AddRange(style.References);

        return new FileExtract([.. nodes])
        {
            Scopes = script.Scopes,
            References = references,
            Imports = imports,
            Literals = script.Literals,
            LiteralsBeyond = script.LiteralsBeyond,
        };
    }

    /// <summary>The blocks of a single-file component.</summary>
    private enum Block
    {
        /// <summary>Outside any block.</summary>
        None,

        /// <summary>A <c>script</c> block.</summary>
        Script,

        /// <summary>A <c>template</c> block.</summary>
        Template,

        /// <summary>A <c>style</c> block.</summary>
        Style,
    }

    /// <summary>One line of a template: components made, handlers called, expressions and classes used.</summary>
    private void Template(string component, List<ExtractReference> references, string line, int at)
    {
        foreach (Match element in Elements().Matches(line))
        {
            var tag = element.Groups["tag"].Value;

            if (tag.Contains('-', StringComparison.Ordinal) || (tag.Length > 0 && char.IsUpper(tag[0])))
            {
                references.Add(new ExtractReference(component, CodeEdgeKinds.Instantiates, tag, at));
            }
        }

        foreach (Match handler in Handlers().Matches(line))
        {
            references.Add(new ExtractReference(component, CodeEdgeKinds.Calls, handler.Groups["name"].Value, at));
        }

        foreach (Match bound in Bindings().Matches(line))
        {
            references.Add(new ExtractReference(component, CodeEdgeKinds.Uses, bound.Groups["name"].Value, at));
        }

        foreach (Match expression in Mustaches().Matches(line))
        {
            if (Expression().Match(expression.Groups["code"].Value) is { Success: true } first)
            {
                references.Add(new ExtractReference(component, CodeEdgeKinds.Uses, first.Groups["name"].Value, at));
            }
        }

        foreach (Match named in Classes().Matches(line))
        {
            foreach (var name in named.Groups["names"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                references.Add(new ExtractReference(component, CodeEdgeKinds.Uses, $".{name}", at) { Qualifier = HtmlExtractor.Selector() });
            }
        }

        foreach (Match byId in Ids().Matches(line))
        {
            if (byId.Groups["names"].Value.Trim() is { Length: > 0 } id)
            {
                references.Add(new ExtractReference(component, CodeEdgeKinds.Uses, $"#{id}", at) { Qualifier = HtmlExtractor.Selector() });
            }
        }
    }

    /// <summary>A block's opening tag.</summary>
    [GeneratedRegex(@"<(?<name>script|template|style)\b", RegexOptions.CultureInvariant)]
    private static partial Regex Opening();

    /// <summary>A block's opening tag closed on the same line, holding nothing.</summary>
    [GeneratedRegex(@"/>", RegexOptions.CultureInvariant)]
    private static partial Regex SelfClosing();

    /// <summary>A block's closing tag.</summary>
    [GeneratedRegex(@"</(script|template|style)\s*>", RegexOptions.CultureInvariant)]
    private static partial Regex Closing();

    /// <summary>An element a template line opens, by its tag.</summary>
    [GeneratedRegex(@"<(?<tag>[A-Za-z][A-Za-z0-9_.\-]*)", RegexOptions.CultureInvariant)]
    private static partial Regex Elements();

    /// <summary>A <c>v-on:name</c> or <c>@name</c> attribute: the handler its value begins with.</summary>
    [GeneratedRegex(@"(?:v-on:|@)\w+\s*=\s*""(?<name>[A-Za-z_$][A-Za-z0-9_$.]*)", RegexOptions.CultureInvariant)]
    private static partial Regex Handlers();

    /// <summary>A <c>v-bind:name</c> or <c>:name</c> attribute: the name its expression begins with.</summary>
    [GeneratedRegex(@"(?::\w+|v-bind:\w+)\s*=\s*""(?<name>[A-Za-z_$][A-Za-z0-9_$.]*)", RegexOptions.CultureInvariant)]
    private static partial Regex Bindings();

    /// <summary>A <c>{{ }}</c> the line holds.</summary>
    [GeneratedRegex(@"\{\{\s*(?<code>.*?)\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex Mustaches();

    /// <summary>The first name an expression begins with.</summary>
    [GeneratedRegex(@"^[!(\s]*(?<name>[A-Za-z_$][A-Za-z0-9_$.]*)", RegexOptions.CultureInvariant)]
    private static partial Regex Expression();

    /// <summary>The names a class attribute holds.</summary>
    [GeneratedRegex(@"class\s*=\s*""(?<names>[^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex Classes();

    /// <summary>An id attribute's name.</summary>
    [GeneratedRegex(@"\bid\s*=\s*""(?<names>[^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex Ids();
}
