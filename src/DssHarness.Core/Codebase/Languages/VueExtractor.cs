using System.Text.RegularExpressions;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// A Vue single-file component (<c>.vue</c>), read through the markup reader every markup file shares
/// (<see cref="MarkupText"/>, <see cref="MarkupDialect.Vue"/>): the component a <c>type</c> SubKind <c>component</c>
/// named by its file - in PascalCase, as Vue names it - and qualified by the file's path, the scope its script declares
/// in, so the script's declarations are the component's members; exported <c>default</c>, so
/// <c>import Foo from './Foo.vue'</c> binds to it and <c>Foo.x</c> to its member <c>x</c>. Its script blocks -
/// <c>&lt;script&gt;</c> and <c>&lt;script setup&gt;</c>, TypeScript where <c>lang="ts"</c> or <c>"tsx"</c> - are read
/// by the JavaScript reader as the one module Vue compiles them to, the script's default export the component itself.
/// Its template's elements, nested templates among them: a component's tag - <c>&lt;FooBar&gt;</c> or
/// <c>&lt;foo-bar&gt;</c>, each naming <c>FooBar</c> - an <c>instantiates</c>; a <c>v-on</c> or <c>@</c> attribute, its
/// modifiers among them, a call of the handler it names; a <c>v-bind</c>, <c>:</c> attribute, another directive's
/// expression - <c>v-model</c>, <c>v-if</c> - a <c>v-for</c>'s list or <c>{{ }}</c> a use of the name its expression
/// begins with - each a member of the component, looked for on it as the template compiles them, but a name a
/// <c>v-for</c> or a <c>v-slot</c> declares where it stands - and each class and id a use of the rule that styles it. Each style block - scoped among them - is read by the style
/// sheet's reader; the script and styles are embedded as every markup embeds code (<see cref="EmbeddedCode"/>).
/// </summary>
public sealed partial class VueExtractor : ICodeGraphExtractor
{
    /// <summary>The name the script's default export is declared by where its expression names none.</summary>
    private const string Default = "default";

    /// <inheritdoc/>
    public string Language => "vue";

    /// <inheritdoc/>
    public int Version => 3;

    /// <inheritdoc/>
    public CodeGraphClaims Claims { get; } = CodeGraphClaims.OfExtensions(".vue");

    /// <inheritdoc/>
    public bool Outlines => false;

    /// <inheritdoc/>
    public CodeGraphExtractorHelp Help { get; } = new(
        "Vue single-file components (.vue): the component a type named by its file and qualified by its path, exported default, so an import of it binds to it and a member through it to its script's declaration; its script blocks - TypeScript where lang=\"ts\" - read by the JavaScript reader as the one module Vue compiles them to, its default export the component itself; its template's component tags - <FooBar> and <foo-bar> alike - as instantiates, its v-on and @ attributes as calls and its v-bind, :, v-model, v-if and every other directive's expression, a v-for's list and {{ }} as uses of the component's members - a v-for's alias and a v-slot's name the template's own, no member - and its classes and ids as uses of the rules that style them; and each style block read by the style sheet's reader",
        [
            "a template expression past the name it begins with, and one that begins with none - an object, a literal: no reference of what it holds",
            "a template written in another language (lang=\"pug\"): not read",
            "a style block's SCSS, Less and its framework-specific syntaxes: read as the style sheet's reader reads them",
            "a script block that names its file by src: not read, and no import of it",
            "a component with no script block: the type alone, and nothing of its own members",
        ]);

    /// <inheritdoc/>
    public FileExtract Extract(CodeFileInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var markup = MarkupText.Read(EmbeddedCode.LineFeeds(input.Text), MarkupDialect.Vue.ClosesAsHtml);
        var lineCount = input.Lines.Count;
        var name = Named(CodeGraphIds.FileName(input.Path));
        // The component is the module its script is: qualified by the scope the script declares in, so the script's
        // declarations are its members, and declared in it, so it sees what the script keeps to itself, as its template does.
        var component = new ExtractNode($"{Language}:{input.Path}", CodeNodeKinds.Type, name, input.Path, 1, Math.Max(lineCount, 1), name)
        {
            SubKind = "component",
            Scope = input.Path,
            Exports = [Default],
        };

        // The blocks the file holds: a template, the scripts and the styles, each an element at its top.
        var blocks = markup.Elements.Where(element => element.Parent < 0).ToList();
        var references = new List<ExtractReference>();

        if (blocks.FirstOrDefault(block => block.Name == "template") is { } template && template.Attribute("lang") is null or "html")
        {
            Template(markup, template, component.Id, lineCount, references);
        }

        var embedded = new List<FileExtract>();
        var scripts = blocks.Where(block => block.Name == "script" && block.Attribute("src") is null && block.Close > block.OpenEnd).ToList();

        if (scripts.Count > 0)
        {
            var languages = scripts.Select(script => script.Attribute("lang")?.Trim().ToLowerInvariant()).ToList();
            var typescript = languages.Any(written => written is "ts" or "tsx");
            var text = EmbeddedCode.Kept(markup.Text, [.. scripts.Select(script => (script.OpenEnd, script.Close))]);

            embedded.Add(Folded(new JsReading(Language, input.Path, text, typescript, markup: !typescript || languages.Contains("tsx"), module: true).Read(), input.Path, component.Id));
        }

        embedded.AddRange(blocks.Where(block => block.Name == "style" && block.Close > block.OpenEnd)
            .Select(style => CssExtractor.Read(Language, new CodeFileInput(input.Path, EmbeddedCode.Kept(markup.Text, [(style.OpenEnd, style.Close)])))));

        return EmbeddedCode.Merged(new FileExtract([component]) { References = references }, embedded);
    }

    /// <summary>
    /// What the template says: each element its markup holds - nested templates among them - read by the markup's one
    /// rule, and each <c>{{ }}</c> its text holds a use of the name its expression begins with, each a member of the
    /// component, looked for on it - but a name the template declares where it stands, a loop's alias or a slot's, which
    /// is no member.
    /// </summary>
    private static void Template(MarkupText markup, MarkupElement template, string component, int lineCount, List<ExtractReference> references)
    {
        var members = new ExtractQualifier(CodeQualifierKinds.This);
        var elements = markup.Elements;

        // The names declared where each element stands: its own loop's aliases and slot's names, and its ancestors'.
        var local = new IReadOnlySet<string>?[elements.Count];

        for (var at = 0; at < elements.Count; at++)
        {
            var element = elements[at];

            if (!markup.Inside(element, template))
            {
                continue;
            }

            var inherited = element.Parent >= 0 ? local[element.Parent] : null;
            var declared = new HashSet<string>(MarkupDialect.Vue.Declared(element), StringComparer.Ordinal);

            if (declared.Count > 0 && inherited is not null)
            {
                declared.UnionWith(inherited);
            }

            local[at] = declared.Count > 0 ? declared : inherited;
            MarkupDialect.Vue.Read(element, component, members, references, local[at]);
        }

        // Each run of text with the element it stands in, found by one sweep over both in the order they are written.
        var open = new List<int>();
        var next = 0;

        foreach (var (start, end) in markup.Texts.Where(text => text.Start >= template.OpenEnd && text.End <= template.Close))
        {
            for (; next < elements.Count && elements[next].Start < start; next++)
            {
                Leave(open, elements[next].Start);
                open.Add(next);
            }

            Leave(open, start);
            var names = open.Count > 0 ? local[open[^1]] : null;

            foreach (Match mustache in Mustaches().Matches(markup.Text[start..end]))
            {
                if (MarkupDialect.Begins(mustache.Groups["code"].Value) is { } name && names?.Contains(name) != true)
                {
                    references.Add(new ExtractReference(component, CodeEdgeKinds.Uses, name, FileExtract.LineWithin(markup.LineAt(start + mustache.Index), lineCount)) { Qualifier = members });
                }
            }
        }

        // The elements open that end before the offset left: what stands there is in none of them.
        void Leave(List<int> held, int offset)
        {
            while (held.Count > 0 && offset >= Math.Max(elements[held[^1]].Close, elements[held[^1]].OpenEnd))
            {
                held.RemoveAt(held.Count - 1);
            }
        }
    }

    /// <summary>
    /// <paramref name="script"/> with its default export made the component it is: the declaration its
    /// <c>export default</c> of an expression makes - the options object of a component written so - is the component,
    /// what it holds and the references it makes now the component's, and each of the script's own declarations that object
    /// names (<c>export default { charge }</c>) is a member the component shows whoever imports it, kept to the script no
    /// more; a declaration the script exports by name besides is no longer exported by <c>default</c>, which the component
    /// answers alone.
    /// </summary>
    private static FileExtract Folded(FileExtract script, string path, string component)
    {
        var folded = script.Nodes.Where(node => node.Name == Default && node.Parent is null && node.Scope == path).Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        var shown = script.References.Where(reference => folded.Contains(reference.From) && reference.Kind == CodeEdgeKinds.Uses && reference.Qualifier is null)
            .Select(reference => reference.Name).ToHashSet(StringComparer.Ordinal);

        return script with
        {
            Nodes = [.. script.Nodes.Where(node => !folded.Contains(node.Id)).Select(node => node with
            {
                Parent = node.Parent is { } parent && folded.Contains(parent) ? component : node.Parent,
                SeenIn = node.Parent is null && node.Scope == path && shown.Contains(node.Name) ? null : node.SeenIn,
                Exports = node.Exports?.Contains(Default, StringComparer.Ordinal) == true
                    ? node.Exports.Where(exported => exported != Default).ToList() is { Count: > 0 } rest ? rest : null
                    : node.Exports,
            })],
            References = [.. script.References.Select(reference => folded.Contains(reference.From) ? reference with { From = component } : reference)],
        };
    }

    /// <summary>The component's name: its file's, in PascalCase as Vue names a component - <c>child-item.vue</c> is <c>ChildItem</c>.</summary>
    private static string Named(string file)
    {
        var simple = file.EndsWith(".vue", StringComparison.OrdinalIgnoreCase) ? file[..^4] : file;
        var pascal = MarkupDialect.Pascal(simple);

        return pascal.Length > 0 ? pascal : simple.Length > 0 ? simple : file;
    }

    /// <summary>A <c>{{ }}</c> a template's text holds, over lines or on one.</summary>
    [GeneratedRegex(@"\{\{(?<code>.*?)\}\}", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex Mustaches();
}
