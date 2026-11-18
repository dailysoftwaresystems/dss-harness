using System.Globalization;
using System.Text.RegularExpressions;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// A Razor view (<c>.cshtml</c>) or a Blazor component (<c>.razor</c>): a component is a <c>type</c> named by its file
/// and its <c>@namespace</c> - its directory's path where it says none - marked a part of its code-behind partial,
/// which the linker joins to it; <c>@model</c>, <c>@inject</c> and <c>@inherits</c> are references, an
/// <c>@inject</c>'s name a member of the value its type names, <c>@using</c> a scope import; the code of
/// <c>@code</c> and <c>@functions</c> blocks is read by the C# reader with the markup blanked, so its declarations
/// carry the lines they stand on; a PascalCase element or a <c>&lt;partial name=</c> an <c>instantiates</c> of a
/// component or view of that name, an <c>@Html.Partial("x")</c> an instantiates of the view it names, an
/// <c>@on*</c> attribute a call of the handler it names and another <c>@</c> attribute a use of the name its value
/// begins with. A view is a <c>section</c> over its lines. A <c>_ViewImports.cshtml</c> and an <c>_Imports.razor</c>
/// apply each <c>@using</c> they hold to every file of the language under their directory.
/// </summary>
public sealed partial class RazorExtractor : ICodeGraphExtractor
{
    /// <summary>The names of the files whose <c>@using</c> reaches a whole directory's subtree.</summary>
    private static readonly HashSet<string> Imports = new(StringComparer.OrdinalIgnoreCase) { "_ViewImports.cshtml", "_Imports.razor" };

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
        "Razor views (.cshtml) and Blazor components (.razor): a component as a type named by its file and its @namespace, a part of its code-behind partial; @model, @inject and @inherits as references, an @inject's name a member; @using as a scope import - one a _ViewImports.cshtml or _Imports.razor holds reaching every file of the language under its directory; @code and @functions read by the C# reader with the markup blanked; a PascalCase element, a partial name= and an @Html.Partial(\"x\") as instantiates; an @on* attribute a call and another @ attribute a use",
        [
            "inline C# expressions in the markup - an @x, a @{ } block - read as text, and a component with no @namespace joins its code-behind only where its directory's path names the partial's namespace",
            "what a tag helper is, and an element no component of the tree names: an instantiates that resolves nowhere, counted as such",
            "a layout a view or component names by @layout: no reference of it",
        ]);

    /// <inheritdoc/>
    public FileExtract Extract(CodeFileInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var lines = input.Lines;
        var name = CodeGraphIds.FileName(input.Path);
        var component = name.EndsWith(".razor", StringComparison.OrdinalIgnoreCase);
        var view = name.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase);
        var nodes = new List<ExtractNode>();
        var references = new List<ExtractReference>();
        var imports = new List<ExtractImport>();
        var ids = new ExtractIds();
        string? namespaceOf = null;
        var code = new List<string>(lines.Count);

        for (var at = 0; at < lines.Count; at++)
        {
            code.Add(string.Empty);
        }

        // The namespace first, for the head of the extract to be named by it: a Blazor component a type and a part of
        // its code-behind partial, a view a section over its lines.
        for (var at = 0; at < lines.Count; at++)
        {
            if (Directive().Match(lines[at].TrimStart()) is { Success: true } named && named.Groups["name"].Value == "namespace")
            {
                namespaceOf = named.Groups["rest"].Value.Trim();
                break;
            }
        }

        var simple = name[..name.LastIndexOf('.')];

        if (component || view)
        {
            var qualified = ExtractText.Qualify(namespaceOf ?? FolderOf(input.Path), simple);
            nodes.Add(component
                ? new ExtractNode(ids.Next($"{Language}:{qualified}"), CodeNodeKinds.Type, simple, qualified, 1, Math.Max(lines.Count, 1), qualified) { SubKind = "component", Part = true }
                : new ExtractNode(ids.Next($"{Language}:{input.Path}#view"), CodeNodeKinds.Section, simple, $"{input.Path}#view", 1, Math.Max(lines.Count, 1), simple) { SubKind = "view" });
        }

        // The directives a line of its own begins, and the code blocks read by the C# reader with the markup blanked.
        // A code block's opening line is the partial class the page or component compiles to - named by its file, in the
        // namespace a component says or its directory's path - so its members are the class's own, at their own lines,
        // and the class itself is a part of its code-behind partial.
        var inside = 0;
        var wrapped = false;

        for (var at = 0; at < lines.Count; at++)
        {
            var line = lines[at];
            var trimmed = line.TrimStart();

            if (inside > 0)
            {
                code[at] = line;
                inside += Count(line, '{') - Count(line, '}');

                if (inside <= 0)
                {
                    inside = 0;
                }

                continue;
            }

            if (Directive().Match(trimmed) is { Success: true } directive)
            {
                switch (directive.Groups["name"].Value)
                {
                    case "using":
                        imports.Add(new ExtractImport(Imports.Contains(name) ? CodeImportForms.Directory : CodeImportForms.Scope, directive.Groups["rest"].Value.Trim(), at + 1));
                        break;

                    case "model" or "inherits" when Plain(directive.Groups["rest"].Value) is { Length: > 0 } written:
                        references.Add(new ExtractReference(nodes[0].Id, CodeEdgeKinds.References, written, at + 1));
                        break;

                    case "inject" when Injected().Match(directive.Groups["rest"].Value.Trim()) is { Success: true } injected:
                        nodes.Add(new ExtractNode(ids.Next($"{Language}:{input.Path}#{injected.Groups["name"].Value}"), CodeNodeKinds.Member, injected.Groups["name"].Value, injected.Groups["name"].Value, at + 1, at + 1, $"@inject {directive.Groups["rest"].Value.Trim()}")
                        {
                            SubKind = "property",
                            Parent = nodes[0].Id,
                            ValueType = Last(injected.Groups["type"].Value.Trim()),
                        });
                        references.Add(new ExtractReference(nodes[0].Id, CodeEdgeKinds.References, Last(injected.Groups["type"].Value.Trim()), at + 1));
                        break;

                    default:
                        break;
                }

                continue;
            }

            if (CodeBlock().Match(trimmed) is { Success: true })
            {
                code[at] = new string(' ', Math.Max(0, line.Length - trimmed.Length)) + $"partial class {simple} {{";
                wrapped = true;
                inside = Math.Max(0, Count(line, '{') - Count(line, '}'));
                continue;
            }

            Markup(nodes, references, line, at + 1);
        }

        var text = string.Join('\n', code);

        if (wrapped)
        {
            // The namespace stands on the file's first line - a file-scoped one, so the code keeps its own lines.
            var scope = namespaceOf ?? FolderOf(input.Path) ?? simple;
            var rest = code.Count > 1 ? string.Join('\n', code[1..]) : string.Empty;
            text = $"namespace {scope};\n{rest}";
        }

        var read = CSharpExtractor.Read(Language, new CodeFileInput(input.Path, text));
        nodes.AddRange(read.Nodes);
        references.AddRange(read.References);
        imports.AddRange(read.Imports);

        // Where the code block's class is the component the file is - a Blazor component's @code - the class the reader
        // found is the node, the one made for it before is not, and the component spans the file: the injects its
        // markup declares are its members as much as the code's own.
        if (component && nodes.Count > 0 && read.Nodes.Any(node => node.Id == nodes[0].Id))
        {
            var componentId = nodes[0].Id;
            nodes.RemoveAt(0);

            var at = nodes.FindIndex(node => node.Id == componentId);

            if (at >= 0)
            {
                nodes[at] = nodes[at] with { Start = 1, End = Math.Max(lines.Count, 1) };

                // The scopes the component is declared in - the namespace a @namespace gives it - hold the file whole,
                // as the component does.
                for (var parent = nodes[at].Parent; parent is not null;)
                {
                    var holder = nodes.FindIndex(node => node.Id == parent);

                    if (holder < 0)
                    {
                        break;
                    }

                    nodes[holder] = nodes[holder] with { End = Math.Max(nodes[holder].End, Math.Max(lines.Count, 1)) };
                    parent = nodes[holder].Parent;
                }
            }
        }

        return new FileExtract([.. nodes])
        {
            References = references,
            Imports = imports,
            Literals = read.Literals,
            LiteralsBeyond = read.LiteralsBeyond,
        };
    }

    /// <summary>What the markup of one line holds: components made, partials named, handlers called and attributes used.</summary>
    private void Markup(List<ExtractNode> nodes, List<ExtractReference> references, string line, int at)
    {
        if (nodes.Count == 0)
        {
            return;
        }

        var from = nodes[0].Id;

        foreach (Match element in Elements().Matches(line))
        {
            var tag = element.Groups["tag"].Value;

            if (char.IsUpper(tag[0]) || tag.Contains('.', StringComparison.Ordinal))
            {
                references.Add(new ExtractReference(from, CodeEdgeKinds.Instantiates, tag, at));
            }
        }

        foreach (Match partial in Partials().Matches(line))
        {
            references.Add(new ExtractReference(from, CodeEdgeKinds.Instantiates, partial.Groups["name"].Value, at));
        }

        foreach (Match call in HtmlPartials().Matches(line))
        {
            references.Add(new ExtractReference(from, CodeEdgeKinds.Instantiates, call.Groups["name"].Value, at));
        }

        foreach (Match handler in Handlers().Matches(line))
        {
            references.Add(new ExtractReference(from, CodeEdgeKinds.Calls, handler.Groups["name"].Value, at));
        }

        foreach (Match bound in Attributes().Matches(line))
        {
            references.Add(new ExtractReference(from, CodeEdgeKinds.Uses, bound.Groups["name"].Value, at));
        }
    }

    /// <summary>The namespace a component with no <c>@namespace</c> is given: its directory's path from the tree's root.</summary>
    private static string? FolderOf(string path)
        => path.LastIndexOf('/') is var slash and > 0 ? path[..slash].Replace('/', '.') : null;

    /// <summary>A directive at a line's start: <c>@name rest</c>.</summary>
    [GeneratedRegex(@"^@(?<name>using|model|inherits|inject|namespace|layout|attribute|page|implements)\s+(?<rest>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex Directive();

    /// <summary>A <c>@code</c> or <c>@functions</c> block's opening.</summary>
    [GeneratedRegex(@"^@(code|functions)\s*\{", RegexOptions.CultureInvariant)]
    private static partial Regex CodeBlock();

    /// <summary>An <c>@inject</c>'s rest: a type and the name it is given.</summary>
    [GeneratedRegex(@"^(?<type>.+?)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex Injected();

    /// <summary>An element a line opens, by its tag.</summary>
    [GeneratedRegex(@"<(?<tag>[A-Za-z][A-Za-z0-9_.\-]*)", RegexOptions.CultureInvariant)]
    private static partial Regex Elements();

    /// <summary>A partial a line names.</summary>
    [GeneratedRegex(@"<partial[^>]*name=""(?<name>[^""]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex Partials();

    /// <summary>An <c>@Html.Partial("x")</c> a line holds.</summary>
    [GeneratedRegex(@"@Html\.Partial\(""(?<name>[^""]+)""\)", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlPartials();

    /// <summary>An <c>@on*</c> attribute's handler: the first name its value begins with.</summary>
    [GeneratedRegex(@"@on\w+\s*=\s*""(?<name>[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.CultureInvariant)]
    private static partial Regex Handlers();

    /// <summary>Another <c>@</c> attribute - an <c>@on*</c> handler's excepted, a call of its own - : the first name its value begins with.</summary>
    [GeneratedRegex(@"@(?!on)\w+\s*=\s*""(?<name>[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.CultureInvariant)]
    private static partial Regex Attributes();

    /// <summary>The plain name <paramref name="written"/> is, its generics and path taken off.</summary>
    private static string Plain(string written)
        => Last(written.Split('<')[0].Trim());

    /// <summary>The last part of a dotted name.</summary>
    private static string Last(string written) => written[(written.LastIndexOf('.') + 1)..];

    /// <summary>How many of <paramref name="character"/> <paramref name="line"/> holds.</summary>
    private static int Count(string line, char character)
    {
        var count = 0;

        foreach (var one in line)
        {
            if (one == character)
            {
                count++;
            }
        }

        return count;
    }
}
