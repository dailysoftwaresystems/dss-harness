using System.Text.RegularExpressions;
using DssHarness.Core.Text;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// A Vue single-file component (<c>.vue</c>), read through the markup reader every markup file shares
/// (<see cref="MarkupText"/>, <see cref="MarkupDialect.Vue"/>): the component a <c>type</c> SubKind <c>component</c>
/// named by its file - in PascalCase, as Vue names it - and qualified by the file's path, the scope its script declares
/// in, so the script's declarations are the component's members; exported <c>default</c>, so
/// <c>import Foo from './Foo.vue'</c> binds to it and <c>Foo.x</c> to its member <c>x</c>. Its script blocks -
/// <c>&lt;script&gt;</c> and <c>&lt;script setup&gt;</c>, TypeScript where <c>lang="ts"</c> or <c>"tsx"</c> - are read
/// by the JavaScript reader as the one module Vue compiles them to, the script's default export the component itself, the
/// members its options declare - its props, injections, methods, computed properties, functions and what <c>data()</c>
/// returns - and the props a <c>&lt;script setup&gt;</c>'s <c>defineProps</c> declares the component's members.
/// A block that names its file by <c>src</c> is an import of it, as a page's is (<see cref="HtmlExtractor.Local"/>). Its
/// template's elements, nested templates among them: a component's tag - <c>&lt;FooBar&gt;</c> or
/// <c>&lt;foo-bar&gt;</c>, each naming <c>FooBar</c> - an <c>instantiates</c>, and each class and id a use of the rule
/// that styles it; and its code - each directive's value, a dynamic argument, a <c>v-for</c>'s list, a slot's pattern,
/// each <c>{{ }}</c> - read by the JavaScript reader as Vue's compiler compiles it (<see cref="MarkupTemplates.Read"/>), each name it
/// reads by itself a member of the component, looked for on it as the template compiles them, but Vue's globals and a
/// name a <c>v-for</c> or a <c>v-slot</c> declares where it stands.
/// Each style block is read by the style sheet's reader in the syntax its <c>lang</c> names: CSS, SCSS, Sass's indented
/// syntax or Less - a <c>&lt;style scoped&gt;</c> as the component's own, a <c>&lt;style module&gt;</c> as a CSS module whose
/// classes the name it gives binds, <c>$style</c> where it gives none (<see cref="Bound"/>), any other as the page's. The
/// script and styles are embedded as every markup embeds code (<see cref="EmbeddedCode"/>).
/// </summary>
public sealed class VueExtractor : ICodeGraphExtractor
{
    /// <summary>The name the script's default export is declared by where its expression names none.</summary>
    private const string Default = "default";

    /// <summary>The name a <c>&lt;style module&gt;</c> that gives none binds its classes by, as Vue binds them.</summary>
    private const string ModuleBinding = "$style";

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
        "Vue single-file components (.vue): the component a type named by its file and qualified by its path, exported default, so an import of it binds to it and a member through it to its script's declaration; its script blocks - TypeScript where lang=\"ts\" - read by the JavaScript reader as the one module Vue compiles them to, its default export the component itself, its options object - or the one defineComponent is given - declaring its members: each prop and injection, method and computed property, each function it holds (data, setup, a lifecycle hook) and each property data() and setup() return a member of the component, as is each prop a <script setup>'s defineProps declares, and each watch key a use of the member it watches; a template, script or style block that names its file by src an import of it; its template's component tags - <FooBar> and <foo-bar> alike - as instantiates and its classes and ids as uses of the rules that style them, and its code read by the JavaScript reader as Vue compiles it - a v-on or @ value that names a member a call of it, a function its handler, anything else statements the event runs with $event; a v-bind, :, v-model, v-if and every other directive's expression, a dynamic argument, a v-for's list and {{ }} what their expressions read - each name read by itself a member of the component, but Vue's globals (Math) and a v-for's alias or a v-slot's name, the template's own, and a slot's defaults read once; and each style block read by the style sheet's reader in the syntax its lang names - CSS where it names none, scss, sass, less - a <style scoped>'s rules the component's own, reached from its template alone, and a <style module>'s classes, ids and keyframes bound by the name it gives ($style where it gives none), so $style.red in the template or this.$style.red in the script is a use of what the module exports as red, exact, and a class=\"red\" none of it",
        [
            "a template written in another language (lang=\"pug\"), and one that names its file by src: not read as the component's - the file is read as its own",
            "a style block of another language (lang=\"stylus\"): not read",
            "a component with no script block: the type alone, and nothing of its own members",
            "what a mixin or an extends option adds, a prop defineModel declares and one defineProps is given by a type another file declares: no member of the component",
            "a module block's classes read through what useCssModule() returns, or by a string ($style['red-x']): no use of them",
            "a file of more than 2,147,483,647 characters, past the Int32 the HTML tokenizer (AngleSharp) counts a position in: nothing in it read, the line its count ends on marked dynamic, with why",
        ])
    {
        Holds = "AngleSharp's text source, which keeps every character the tokenizer decodes - its HTML tokenizer reads a source of its buffered kind alone (a TextSource over a stream), and the source that lets go of what it read is offered to its parser alone, which builds a document rather than the tokens this reader matches - so reading a file holds its markup's text once, in pieces, never as one string",
    };

    /// <inheritdoc/>
    public FileExtract Extract(CodeFileInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var fed = input.Text.LineFed();

        if (MarkupText.Unread(fed) is { } unread)
        {
            return unread;
        }

        var markup = MarkupText.Read(fed, MarkupDialect.Vue.ClosesAsHtml);
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

        // The blocks the file holds: a template, the scripts and the styles, each an element at its top - one that names
        // its file by src that file's, an import of it as a page's src is.
        var blocks = markup.Elements.Where(element => element.Parent < 0).ToList();
        var references = new List<ExtractReference>();
        var imports = new List<ExtractImport>();
        var embedded = new List<FileExtract>();

        foreach (var block in blocks.Where(block => block.Name is "template" or "script" or "style"))
        {
            if (block.Attributes.FirstOrDefault(attribute => attribute.Name == "src") is { Value.Length: > 0 } source && HtmlExtractor.Local(source.Value) is { } target)
            {
                imports.Add(new ExtractImport(CodeImportForms.Relative, target, FileExtract.LineWithin(source.Line, lineCount)));
            }
        }

        var scripts = blocks.Where(block => block.Name == "script" && block.Attribute("src") is null && block.Close > block.OpenEnd).ToList();
        var languages = scripts.Select(script => script.Attribute("lang")?.Trim().ToLowerInvariant()).ToList();
        var typescript = languages.Any(written => written is "ts" or "tsx");

        if (blocks.FirstOrDefault(block => block.Name == "template") is { } template && template.Attribute("src") is null && template.Attribute("lang") is null or "html"
            && MarkupTemplates.Read(markup, MarkupDialect.Vue, component.Id, references, template) is { Count: > 0 } regions)
        {
            embedded.Add(MarkupTemplates.Members(EmbeddedCode.Script(Language, input.Path, markup.Text, markup.Lines, regions, typescript)));
        }

        if (scripts.Count > 0)
        {
            var text = EmbeddedCode.Kept(markup.Text, markup.Lines, [.. scripts.Select(script => (script.OpenEnd, script.Close))]);

            embedded.Add(Folded(new JsReading(Language, input.Path, text, typescript, markup: !typescript || languages.Contains("tsx"), module: true, component: true).Read(), input.Path, component.Id));
        }

        // Each style block: a module's names its own, kept to a scope of its binding's - $style where it gives none -
        // which the binding names; a scoped one's this component's alone; any other the page's.
        var bindings = new Dictionary<string, QualifiedName>(StringComparer.Ordinal);

        foreach (var style in blocks.Where(block => block.Name == "style" && block.Attribute("src") is null && block.Close > block.OpenEnd))
        {
            var module = style.Attribute("module");
            var binding = module?.Trim() is { Length: > 0 } named ? named : ModuleBinding;
            var scope = module is not null ? StyleScope.Module : style.Attribute("scoped") is not null ? StyleScope.Scoped : StyleScope.Global;
            var kept = module is not null ? QualifiedName.Of($"{input.Path}#{binding}") : null;
            var text = EmbeddedCode.Kept(markup.Text, markup.Lines, [(style.OpenEnd, style.Close)]);

            if (CssExtractor.Read(Language, new CodeFileInput(input.Path, text), style.Attribute("lang"), style.Attribute("type"), scope, kept) is { } read)
            {
                embedded.Add(read);

                if (kept is not null)
                {
                    bindings[binding] = kept;
                }
            }
        }

        return Bound(EmbeddedCode.Merged(new FileExtract([component]) { References = references, Imports = imports }, embedded), bindings);
    }

    /// <summary>
    /// <paramref name="merged"/> with each member a module block's binding reads - <c>$style.red</c> in the template, a
    /// script's <c>this.$style.red</c>, <c>classes.red</c> of a <c>&lt;style module="classes"&gt;</c> - a use of what that
    /// block exports by its name, as Vue binds a module's classes, ids and keyframes: through the scope the block keeps
    /// its own names to (<see cref="CodeQualifierKinds.Module"/>), so the linker finds each by the names the module exports
    /// it by - a suffix written onto others' by those it writes - and nothing else of that name.
    /// </summary>
    /// <param name="merged">The component's extract, every reader's merged.</param>
    /// <param name="bindings">The scope each module block's own names are kept to, by the name it is bound by.</param>
    private static FileExtract Bound(FileExtract merged, IReadOnlyDictionary<string, QualifiedName> bindings)
        => bindings.Count == 0 ? merged : merged with
        {
            References = [.. merged.References.Select(reference => reference is { Kind: CodeEdgeKinds.Uses, Qualifier: { Kind: CodeQualifierKinds.Value, Name: { } binding } }
                && bindings.TryGetValue(binding.Whole(), out var kept)
                    ? reference with { Qualifier = new ExtractQualifier(CodeQualifierKinds.Module, kept) }
                    : reference)],
        };

    /// <summary>
    /// <paramref name="script"/> with its default export made the component it is: the declaration its
    /// <c>export default</c> of an expression makes - the options object of a component written so - is folded into the
    /// component (<see cref="EmbeddedCode.Folded"/>), the members its options declare, the references it makes and the
    /// literals it holds now the component's, and each of the script's own declarations that object names
    /// (<c>export default { charge }</c>) is a member the component shows whoever imports it, kept to the script no more; a
    /// declaration the script exports by name besides is no longer exported by <c>default</c>, which the component answers
    /// alone.
    /// </summary>
    private static FileExtract Folded(FileExtract script, string path, QualifiedName component)
    {
        var folded = script.Nodes.Where(node => node.Name == Default && node.Parent is null && node.Scope is { } scope && scope.Is(path)).Select(node => node.Id).ToHashSet();
        var shown = script.References.Where(reference => folded.Contains(reference.From) && reference.Kind == CodeEdgeKinds.Uses && reference.Qualifier is null)
            .Select(reference => reference.Name).ToHashSet(StringComparer.Ordinal);

        var answered = script with
        {
            Nodes = [.. script.Nodes.Select(node => node with
            {
                SeenIn = node.Parent is null && node.Scope is { } within && within.Is(path) && shown.Contains(node.Name) ? null : node.SeenIn,
                Exports = node.Exports?.Contains(Default, StringComparer.Ordinal) == true && !folded.Contains(node.Id)
                    ? node.Exports.Where(exported => exported != Default).ToList() is { Count: > 0 } rest ? rest : null
                    : node.Exports,
            })],
        };

        return EmbeddedCode.Folded(answered, node => folded.Contains(node.Id) ? component : null);
    }

    /// <summary>The component's name: its file's, in PascalCase as Vue names a component - <c>child-item.vue</c> is <c>ChildItem</c>.</summary>
    private static string Named(string file)
    {
        var simple = file.EndsWith(".vue", StringComparison.OrdinalIgnoreCase) ? file[..^4] : file;
        var pascal = MarkupDialect.Pascal(simple);

        return pascal.Length > 0 ? pascal : simple.Length > 0 ? simple : file;
    }
}
