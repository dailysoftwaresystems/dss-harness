using System.Text.RegularExpressions;

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
/// each <c>{{ }}</c> - read by the JavaScript reader as Vue's compiler compiles it (<see cref="Template"/>), each name it
/// reads by itself a member of the component, looked for on it as the template compiles them, but Vue's globals and a
/// name a <c>v-for</c> or a <c>v-slot</c> declares where it stands.
/// Each style block is read by the style sheet's reader in the syntax its <c>lang</c> names: CSS, SCSS, Sass's indented
/// syntax or Less - a <c>&lt;style scoped&gt;</c> as the component's own, a <c>&lt;style module&gt;</c> as a CSS module whose
/// classes the name it gives binds, <c>$style</c> where it gives none (<see cref="Bound"/>), any other as the page's. The
/// script and styles are embedded as every markup embeds code (<see cref="EmbeddedCode"/>).
/// </summary>
public sealed partial class VueExtractor : ICodeGraphExtractor
{
    /// <summary>The name the script's default export is declared by where its expression names none.</summary>
    private const string Default = "default";

    /// <summary>The name a <c>&lt;style module&gt;</c> that gives none binds its classes by, as Vue binds them.</summary>
    private const string ModuleBinding = "$style";

    /// <summary>The globals Vue's template compiler leaves to themselves, as its own list has them: no member of the component.</summary>
    private static readonly HashSet<string> Globals = new(StringComparer.Ordinal)
    {
        "Infinity", "undefined", "NaN", "isFinite", "isNaN", "parseFloat", "parseInt", "decodeURI", "decodeURIComponent", "encodeURI",
        "encodeURIComponent", "Math", "Number", "Date", "Array", "Object", "Boolean", "String", "RegExp", "Map", "Set", "JSON", "Intl", "BigInt",
        "console", "Error", "Symbol",
    };

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
            && Template(markup, template, component.Id, references) is { Count: > 0 } regions)
        {
            embedded.Add(Members(EmbeddedCode.Script(Language, input.Path, markup.Lines, regions, typescript)));
        }

        if (scripts.Count > 0)
        {
            var text = EmbeddedCode.Kept(markup.Text, markup.Lines, [.. scripts.Select(script => (script.OpenEnd, script.Close))]);

            embedded.Add(Folded(new JsReading(Language, input.Path, text, typescript, markup: !typescript || languages.Contains("tsx"), module: true, component: true).Read(), input.Path, component.Id));
        }

        // Each style block: a module's names its own, kept to a scope of its binding's - $style where it gives none -
        // which the binding names; a scoped one's this component's alone; any other the page's.
        var bindings = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var style in blocks.Where(block => block.Name == "style" && block.Attribute("src") is null && block.Close > block.OpenEnd))
        {
            var module = style.Attribute("module");
            var binding = module?.Trim() is { Length: > 0 } named ? named : ModuleBinding;
            var scope = module is not null ? StyleScope.Module : style.Attribute("scoped") is not null ? StyleScope.Scoped : StyleScope.Global;
            var kept = module is not null ? $"{input.Path}#{binding}" : null;
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
    private static FileExtract Bound(FileExtract merged, IReadOnlyDictionary<string, string> bindings)
        => bindings.Count == 0 ? merged : merged with
        {
            References = [.. merged.References.Select(reference => reference is { Kind: CodeEdgeKinds.Uses, Qualifier: { Kind: CodeQualifierKinds.Value, Name: { } binding } }
                && bindings.TryGetValue(binding, out var kept)
                    ? reference with { Qualifier = new ExtractQualifier(CodeQualifierKinds.Module, kept) }
                    : reference)],
        };

    /// <summary>
    /// What the template says, in one walk over its elements and its texts in the order they are written. Each element -
    /// nested templates among them - is read by the markup's one rule, its tag and its classes and id references of the
    /// component's; and the code its attributes and its text's <c>{{ }}</c> hold is given the JavaScript reader as Vue's
    /// compiler writes it (<see cref="Handler"/>), each piece the body of a function of its own whose parameters are the
    /// names the template declares where it stands that it names (<see cref="DeclaredNames"/>): the aliases of each
    /// <c>v-for</c> around it - for that element's own attributes too, its list read outside them - and the names each
    /// <c>v-slot</c> around it gives what it holds, its pattern a function's parameters where it is written, so its
    /// defaults are code read once; answers those functions, each the component's.
    /// </summary>
    private static List<CodeRegion> Template(MarkupText markup, MarkupElement template, string component, List<ExtractReference> references)
    {
        var elements = markup.Elements;
        var regions = new List<CodeRegion>();
        var declared = new DeclaredNames();
        var texts = markup.Texts.Where(text => text.Start >= template.OpenEnd && text.End <= template.Close).ToList();
        var nextText = 0;

        for (var at = 0; at <= elements.Count; at++)
        {
            var start = at < elements.Count ? elements[at].Start : int.MaxValue;

            // Each run of text before the element, in the elements around it.
            for (; nextText < texts.Count && texts[nextText].Start < start; nextText++)
            {
                var (textStart, textEnd) = texts[nextText];
                declared.Leave(textStart);

                foreach (Match mustache in Mustaches().Matches(markup.Text[textStart..textEnd]))
                {
                    if (mustache.Groups["code"] is { Value.Length: > 0 } code && !string.IsNullOrWhiteSpace(code.Value))
                    {
                        Add(textStart + code.Index, $"({code.Value});");
                    }
                }
            }

            if (at == elements.Count)
            {
                break;
            }

            var element = elements[at];

            if (!markup.Inside(element, template))
            {
                continue;
            }

            var end = Math.Max(element.Close, element.OpenEnd);
            declared.Leave(element.Start);
            MarkupDialect.Vue.Read(element, component, references);

            // A loop's list is read in the scope around the element; its aliases are the element's own, its attributes' too.
            var lists = new Dictionary<MarkupAttribute, CodeRegion>();
            var aliases = new List<string>();

            foreach (var attribute in element.Attributes)
            {
                if (MarkupDialect.Vue.Role(attribute) == MarkupRole.Loop && MarkupDialect.Loop(attribute.Value) is var (bound, list, listAt))
                {
                    lists[attribute] = Piece(attribute.ValueAt + listAt, $"({list});");
                    aliases.AddRange(JsReading.Binds(bound));
                }
            }

            declared.Declare(aliases, end);
            var slots = new List<string>();

            foreach (var attribute in element.Attributes)
            {
                // A directive's dynamic argument - :[key], @[event], #[slot] - is an expression of its own, as written.
                if (MarkupDialect.Vue.Role(attribute) != MarkupRole.None && Argument(markup.Text, attribute) is var (argumentAt, argument))
                {
                    Add(argumentAt, $"({argument});");
                }

                if (string.IsNullOrWhiteSpace(attribute.Value))
                {
                    continue;
                }

                switch (MarkupDialect.Vue.Role(attribute))
                {
                    case MarkupRole.Loop when lists.TryGetValue(attribute, out var listed):
                        regions.Add(listed);
                        break;

                    case MarkupRole.Handler:
                        Add(attribute.ValueAt, Handler(attribute.Value));
                        break;

                    case MarkupRole.Binding:
                        Add(attribute.ValueAt, $"({attribute.Value});");
                        break;

                    // A slot's pattern is a function's parameters, read where it is written; the names it binds are what
                    // the element holds.
                    case MarkupRole.Declares:
                        var given = declared.Given(attribute.Value);
                        regions.Add(new CodeRegion(component, given.Length > 0 ? $"{given}, {attribute.Value}" : attribute.Value, attribute.ValueAt, string.Empty));
                        slots.AddRange(JsReading.Binds(attribute.Value));
                        break;

                    default:
                        break;
                }
            }

            declared.Declare(slots, end);
        }

        return regions;

        // A piece of code, in a function of the names declared where it stands that it names.
        CodeRegion Piece(int offset, string code) => new(component, declared.Given(code), offset, code);

        void Add(int offset, string code) => regions.Add(Piece(offset, code));
    }

    /// <summary>
    /// The dynamic argument a directive's name holds - <c>v-bind:[key]</c>, <c>@[event]</c>, <c>#[slot]</c> - read where the
    /// text writes it, in its own case, as Vue's compiler reads it: the expression between the brackets after the directive's
    /// name, with where it begins; <see langword="null"/> where the name holds none.
    /// </summary>
    private static (int At, string Code)? Argument(string text, MarkupAttribute attribute)
    {
        var written = text.AsSpan(attribute.NameAt, Math.Min(attribute.Name.Length, text.Length - attribute.NameAt));
        var open = written.IndexOf('[');
        var close = written.LastIndexOf(']');

        return open > 0 && close > open + 1 && written[open - 1] is ':' or '@' or '#' or '.' ? (attribute.NameAt + open + 1, written[(open + 1)..close].ToString()) : null;
    }

    /// <summary>
    /// The JavaScript a <c>v-on</c>'s value is compiled to, as Vue's compiler writes it: a member path - <c>pay</c>,
    /// <c>cart.add</c> - the handler it names, called with what the event gives; a function expression the handler itself;
    /// anything else statements the event runs, its value <c>$event</c>.
    /// </summary>
    private static string Handler(string value)
        => MemberPath().IsMatch(value) ? $"((...args) => {value}(...args));"
            : FunctionExpression().IsMatch(value) ? $"({value});"
            : $"(function ($event) {{ {value} }});";

    /// <summary>
    /// <paramref name="template"/>, what the JavaScript reader made of the template's code, with each name it reads by
    /// itself - no receiver's, no local's, which the reader keeps to itself - a member of the component, as Vue's compiler
    /// reads it from the component's instance; but the globals its compiler leaves to themselves.
    /// </summary>
    private static FileExtract Members(FileExtract template)
        => template with
        {
            References = [.. template.References.Select(reference => reference is { Qualifier: null, Kind: CodeEdgeKinds.Uses or CodeEdgeKinds.Calls } && !Globals.Contains(reference.Name)
                ? reference with { Qualifier = new ExtractQualifier(CodeQualifierKinds.This) }
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
    private static FileExtract Folded(FileExtract script, string path, string component)
    {
        var folded = script.Nodes.Where(node => node.Name == Default && node.Parent is null && node.Scope == path).Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        var shown = script.References.Where(reference => folded.Contains(reference.From) && reference.Kind == CodeEdgeKinds.Uses && reference.Qualifier is null)
            .Select(reference => reference.Name).ToHashSet(StringComparer.Ordinal);

        var answered = script with
        {
            Nodes = [.. script.Nodes.Select(node => node with
            {
                SeenIn = node.Parent is null && node.Scope == path && shown.Contains(node.Name) ? null : node.SeenIn,
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

    /// <summary>A <c>{{ }}</c> a template's text holds, over lines or on one.</summary>
    [GeneratedRegex(@"\{\{(?<code>.*?)\}\}", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex Mustaches();

    /// <summary>A member path, as Vue's compiler tells a handler a <c>v-on</c> names from code it runs: names, each after a dot, an optional chain's <c>?.</c> or between brackets.</summary>
    [GeneratedRegex(@"^\s*[A-Za-z_$][\w$]*(?:\??\.[A-Za-z_$][\w$]*|\['[^']*?'\]|\[""[^""]*?""\]|\[\d+\]|\[[A-Za-z_$][\w$]*\])*\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex MemberPath();

    /// <summary>A function expression, as Vue's compiler tells one a <c>v-on</c> gives: an arrow's head, or a <c>function</c>'s.</summary>
    [GeneratedRegex(@"^\s*(?:async\s*)?(?:\([^)]*?\)|[\w$]+)\s*(?::[^=]+)?=>|^\s*(?:async\s+)?function(?:\s+[\w$]+)?\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex FunctionExpression();
}
