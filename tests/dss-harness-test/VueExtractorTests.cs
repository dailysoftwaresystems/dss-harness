using DssHarness.Core.Codebase;
using DssHarness.Core.Codebase.Languages;

namespace DssHarness.Tests;

/// <summary>A Vue single-file component, held to the contract's kit, and the rules of its own the kit does not name.</summary>
public sealed class VueExtractorTests : CodeGraphExtractorContractTests<VueExtractor>
{
    private const string Cart = """
        <template>
          <div id="cart" class="panel wide">
            <checkout-widget :total="total" @click="pay" />
            <span>{{ item.name }}</span>
          </div>
        </template>

        <script setup lang="ts">
        import Billing from "./Billing.vue";

        const total = computed(() => 4);

        function pay() {
          Billing.charge("done");
        }
        </script>

        <style scoped>
        .panel { padding: var(--gap); }
        </style>
        """;

    private const string Billing = """
        <script>
        function charge(amount) {
            return amount;
        }

        export default { charge };
        </script>
        """;

    private const string Panel = """
        <script
          setup
          lang='ts'>
        interface Props { title: string }
        const count = ref<number>(0);
        const same = <T,>(value: T) => value;
        function save() {}
        function submit() {}
        eval(code);
        </script>
        <template>
          <div :class="{ active: isActive }" data-id="5">
            <template v-if="ok"><child-item /></template>
            <Other v-on:click="save" />
            <Comp :model-value="count" @click.prevent="submit" />
            <!-- <OldThing /> -->
          </div>
        </template>
        <style>
        @import "./theme.css";
        </style>
        """;

    /// <inheritdoc/>
    public override IReadOnlyList<CodeGraphFixture> Fixtures { get; } =
    [
        new("src/Cart.vue", Cart)
        {
            Nodes =
            [
                new("vue:src/Cart.vue", CodeNodeKinds.Type, (1, 20)),
                new("vue:src/Cart.vue.pay", CodeNodeKinds.Member, (13, 15)),
                new("vue:src/Cart.vue#.panel", CodeNodeKinds.Member, (19, 19)),
            ],
            Edges =
            [
                new("vue:src/Cart.vue", "vue:src/Cart.vue.pay", CodeEdgeKinds.Calls, 3, CodeConfidences.Exact),
                new("vue:src/Cart.vue", "vue:src/Cart.vue.total", CodeEdgeKinds.Uses, 3, CodeConfidences.Exact),
                new("vue:src/Cart.vue.pay", "vue:src/Billing.vue.charge", CodeEdgeKinds.Calls, 14, CodeConfidences.Exact),
            ],
        },
        new("src/Billing.vue", Billing)
        {
            Nodes = [new("vue:src/Billing.vue", CodeNodeKinds.Type, (1, 7)), new("vue:src/Billing.vue.charge", CodeNodeKinds.Member, (2, 4))],
        },
        new("src/Panel.vue", Panel)
        {
            Nodes = [new("vue:src/Panel.vue", CodeNodeKinds.Type, (1, 21)), new("vue:src/Panel.vue.Props", CodeNodeKinds.Type, (4, 4)), new("vue:src/Panel.vue.save", CodeNodeKinds.Member, (7, 7))],
            Edges =
            [
                new("vue:src/Panel.vue", "vue:src/child-item.vue", CodeEdgeKinds.Instantiates, 13, CodeConfidences.Unique),
                new("vue:src/Panel.vue", "vue:src/Panel.vue.save", CodeEdgeKinds.Calls, 14, CodeConfidences.Exact),
                new("vue:src/Panel.vue", "vue:src/Panel.vue.submit", CodeEdgeKinds.Calls, 15, CodeConfidences.Exact),
                new("vue:src/Panel.vue", "vue:src/Panel.vue.count", CodeEdgeKinds.Uses, 15, CodeConfidences.Exact),
            ],
        },
        new("src/child-item.vue", """
            <template><span class="child">{{ label }}</span></template>
            <script setup>
            const label = "child";
            </script>
            """)
        {
            Nodes = [new("vue:src/child-item.vue", CodeNodeKinds.Type, (1, 4))],
        },
    ];

    /// <summary>
    /// The component is a type named by its file, qualified by its path and exported default; its script - TypeScript
    /// where <c>lang="ts"</c> - is read by the JavaScript reader, its declarations and literals on the lines they stand on;
    /// its template's component tags are instantiates, its <c>@</c> attributes calls, its <c>:</c> attributes and
    /// <c>{{ }}</c> uses of the name they begin with - each a member of the component - and its classes and ids uses of
    /// the rules that style them; its style block is read by the style sheet's reader.
    /// </summary>
    [Fact]
    public void AComponentIsAType_ItsScriptReadItsTemplateScannedItsStyleRead()
    {
        var extract = Read("src/Cart.vue", Cart);
        var component = extract.Nodes[0];

        Assert.Equal(("vue:src/Cart.vue", "Cart", "src/Cart.vue", "component"), (component.Id, component.Name, component.Qualified, component.SubKind));
        Assert.Equal(["default"], component.Exports);

        var pay = Assert.Single(extract.Nodes, node => node.Name == "pay");

        Assert.Equal((13, 15, "src/Cart.vue"), (pay.Start, pay.End, pay.Scope));
        Assert.Contains(new ExtractLiteral("done", 14), extract.Literals ?? []);

        var members = new ExtractQualifier(CodeQualifierKinds.This);

        Assert.Equal(
            [
                new ExtractReference("vue:src/Cart.vue", CodeEdgeKinds.Uses, "#cart", 2) { Qualifier = MarkupDialect.Selector },
                new ExtractReference("vue:src/Cart.vue", CodeEdgeKinds.Uses, ".panel", 2) { Qualifier = MarkupDialect.Selector },
                new ExtractReference("vue:src/Cart.vue", CodeEdgeKinds.Uses, ".wide", 2) { Qualifier = MarkupDialect.Selector },
                new ExtractReference("vue:src/Cart.vue", CodeEdgeKinds.Instantiates, "CheckoutWidget", 3),
                new ExtractReference("vue:src/Cart.vue", CodeEdgeKinds.Uses, "total", 3) { Qualifier = members },
                new ExtractReference("vue:src/Cart.vue", CodeEdgeKinds.Calls, "pay", 3) { Qualifier = members },
                new ExtractReference("vue:src/Cart.vue", CodeEdgeKinds.Uses, "item", 4) { Qualifier = members },
            ],
            extract.References.Where(reference => reference.Line is >= 2 and <= 5));

        var rule = Assert.Single(extract.Nodes, node => node.Id == "vue:src/Cart.vue#.panel");

        Assert.Equal((19, 19), (rule.Start, rule.End));
        Assert.Equal([("--gap", 19)], extract.References.Where(reference => reference.Line == 19).Select(reference => (reference.Name, reference.Line)));
    }

    /// <summary>
    /// A script is TypeScript where its own start tag says so - however the tag is written, over lines or with single
    /// quotes, and whatever block follows it - so its types are nodes and a type argument is no markup.
    /// </summary>
    [Fact]
    public void AScriptIsTypeScript_WhereItsOwnTagSays_WhereverItStands()
    {
        var extract = Read("src/Panel.vue", Panel);
        var props = Assert.Single(extract.Nodes, node => node.Name == "Props");

        Assert.Equal(("vue:src/Panel.vue.Props", CodeNodeKinds.Type, "interface", 4), (props.Id, props.Kind, props.SubKind, props.Start));

        // A generic arrow function's type parameters are no markup: what follows it is read.
        Assert.Equal([("same", 6), ("save", 7), ("submit", 8)], extract.Nodes.Where(node => node.Name is "same" or "save" or "submit").Select(node => (node.Name, node.Start)));
    }

    /// <summary>
    /// A template nested in the template ends nothing, and each attribute is read by its own name: a long-form handler is
    /// one call, a modifier and a dashed binding are read, a bound class and a data attribute are no selectors, and what a
    /// comment holds is no element.
    /// </summary>
    [Fact]
    public void ANestedTemplateEndsNothing_AndEachAttributeIsReadByItsName()
    {
        var extract = Read("src/Panel.vue", Panel);

        Assert.Equal(
            [
                ("ok", CodeEdgeKinds.Uses, 13), ("ChildItem", CodeEdgeKinds.Instantiates, 13), ("Other", CodeEdgeKinds.Instantiates, 14), ("save", CodeEdgeKinds.Calls, 14),
                ("Comp", CodeEdgeKinds.Instantiates, 15), ("count", CodeEdgeKinds.Uses, 15), ("submit", CodeEdgeKinds.Calls, 15),
            ],
            extract.References.Where(reference => reference.Line is >= 11 and <= 18).Select(reference => (reference.Name, reference.Kind, reference.Line)));
    }

    /// <summary>
    /// An import of a component binds to its default export, the component: a use of what it binds is an edge to the
    /// component's node, and a member through it is the component's member - the script's own declaration.
    /// </summary>
    [Fact]
    public void AnImportOfAComponent_BindsToTheComponentItExportsByDefault()
    {
        var linked = CodeGraphLinker.Link(
        [
            new CodeGraphFile("src/Cart.vue", "vue", Read("src/Cart.vue", Cart)),
            new CodeGraphFile("src/Billing.vue", "vue", Read("src/Billing.vue", Billing)),
            new CodeGraphFile("src/Shop.vue", "vue", Read("src/Shop.vue", """
                <script>
                import Billing from "./Billing.vue";
                export default { components: { Billing } };
                </script>
                """)),
        ]);

        Assert.Contains(new CodeEdge("file:src/Cart.vue", "file:src/Billing.vue", CodeEdgeKinds.Imports, 9, CodeConfidences.Exact), linked.Edges);
        Assert.Equal(
            [new CodeEdge("vue:src/Cart.vue.pay", "vue:src/Billing.vue.charge", CodeEdgeKinds.Calls, 14, CodeConfidences.Exact)],
            linked.Edges.Where(edge => edge.From == "vue:src/Cart.vue.pay" && edge.To.StartsWith("vue:src/Billing.vue", StringComparison.Ordinal)));
        Assert.Equal(
            [new CodeEdge("vue:src/Shop.vue", "vue:src/Billing.vue", CodeEdgeKinds.Uses, 3, CodeConfidences.Exact)],
            linked.Edges.Where(edge => edge.From.StartsWith("vue:src/Shop.vue", StringComparison.Ordinal) && edge.Kind == CodeEdgeKinds.Uses));
    }

    /// <summary>A script's default export is the component itself: no second node is exported by default beside it.</summary>
    [Fact]
    public void AScriptsDefaultExport_IsTheComponent()
    {
        var extract = Read("src/Billing.vue", Billing);

        Assert.Equal(["vue:src/Billing.vue"], extract.Nodes.Where(node => node.Name == "default" || node.Exports?.Contains("default") == true).Select(node => node.Id));
        Assert.Equal([("vue:src/Billing.vue", "charge", 6)], extract.References.Select(reference => (reference.From, reference.Name, reference.Line)));
    }

    /// <summary>
    /// A component named as an element whose text HTML reads raw - a <c>Title</c> - holds markup as any component does,
    /// for its tag is matched as written; a <c>textarea</c> as written is that element, its text read for what it binds.
    /// </summary>
    [Fact]
    public void AComponentNamedAsARawTextElement_HoldsMarkup()
    {
        var extract = Read("src/Page.vue", "<template>\n  <Title><child-item /></Title>\n  <textarea>{{ note }}<b-tag /></textarea>\n</template>\n");
        var members = new ExtractQualifier(CodeQualifierKinds.This);

        Assert.Equal(
            [
                new ExtractReference("vue:src/Page.vue", CodeEdgeKinds.Instantiates, "Title", 2),
                new ExtractReference("vue:src/Page.vue", CodeEdgeKinds.Instantiates, "ChildItem", 2),
                new ExtractReference("vue:src/Page.vue", CodeEdgeKinds.Uses, "note", 3) { Qualifier = members },
            ],
            extract.References);
    }

    /// <summary>
    /// Every directive whose value is an expression - <c>v-model</c> with its modifiers, <c>v-if</c>, one of the
    /// component's own - is a use of the name it begins with, and a <c>v-for</c> one of the list it walks; a name a
    /// <c>v-for</c>'s aliases or a slot's value declares is the template's own where it stands - in the element and what
    /// it holds, its text among it, and nowhere after its end - and no member's, though a property a slot's pattern
    /// renames, and a name its default value gives, is.
    /// </summary>
    [Fact]
    public void EveryDirectivesExpression_IsAUse_AndANameTheTemplateDeclaresIsNoMember()
    {
        var extract = Read("src/List.vue", """
            <template>
              <ul>
                <li v-for="(item, index) in items" :key="index" @click="pick(item)">{{ item.name }} {{ total }}</li>
              </ul>
              <input v-model.trim="name" v-if="shown" v-focus="target">
              <Table v-slot="{ row: line, size = fallback }">{{ line }} {{ row }} {{ size }} {{ fallback }}</Table>
              <Panel #header="slot">{{ slot.title }}</Panel>
              <p>{{ item }}</p>
              <div><Panel #footer="note">x</Panel>{{ note }}</div>
            </template>
            """);

        Assert.Equal(
            [
                ("items", CodeEdgeKinds.Uses, 3), ("pick", CodeEdgeKinds.Calls, 3), ("name", CodeEdgeKinds.Uses, 5), ("shown", CodeEdgeKinds.Uses, 5),
                ("target", CodeEdgeKinds.Uses, 5), ("Table", CodeEdgeKinds.Instantiates, 6), ("Panel", CodeEdgeKinds.Instantiates, 7), ("Panel", CodeEdgeKinds.Instantiates, 9),
                ("total", CodeEdgeKinds.Uses, 3), ("row", CodeEdgeKinds.Uses, 6), ("fallback", CodeEdgeKinds.Uses, 6), ("item", CodeEdgeKinds.Uses, 8), ("note", CodeEdgeKinds.Uses, 9),
            ],
            extract.References.Select(reference => (reference.Name, reference.Kind, reference.Line)));
    }

    /// <summary>A dashed tag names the component its PascalCase form does, and a component's file in either case names it so.</summary>
    [Fact]
    public void ADashedTag_NamesTheComponentItsPascalCaseFormDoes()
    {
        var child = Read("src/child-item.vue", "<template><span /></template>");
        var linked = CodeGraphLinker.Link(
        [
            new CodeGraphFile("src/Panel.vue", "vue", Read("src/Panel.vue", Panel)),
            new CodeGraphFile("src/child-item.vue", "vue", child),
        ]);

        Assert.Equal("ChildItem", child.Nodes[0].Name);
        Assert.Equal(
            [new CodeEdge("vue:src/Panel.vue", "vue:src/child-item.vue", CodeEdgeKinds.Instantiates, 13, CodeConfidences.Unique)],
            linked.Edges.Where(edge => edge.Kind == CodeEdgeKinds.Instantiates));
    }

    /// <summary>What a style block imports is the component's import, and what its script's reader marks dynamic is the component's to say: nothing an embedded reader saw is lost.</summary>
    [Fact]
    public void AStyleBlocksImports_AndAScriptsDynamicCode_AreTheComponents()
    {
        var extract = Read("src/Panel.vue", Panel);
        var alone = new TypeScriptExtractor().Extract(new CodeFileInput("src/a.ts", "\n\n\n\n\n\n\n\neval(code);\n"));

        Assert.Equal([("./theme.css", 20)], extract.Imports.Select(import => (import.Target, import.Line)));
        Assert.Equal(alone.Dynamic, extract.Dynamic);
        Assert.Equal(9, Assert.Single(extract.Dynamic).Line);
    }

    private static FileExtract Read(string path, string text) => new VueExtractor().Extract(new CodeFileInput(path, text));
}
