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

    private const string Options = """
        <template>
          <div>{{ msg }}</div>
        </template>
        <script>
        export default {
          name: "CartView",
          props: ["total"],
          data() { return { msg: "hello" } }
        }
        </script>
        """;

    private const string Pay = """
        <template>
          <button @click="pay">{{ total }} {{ msg }} {{ label }}</button>
        </template>
        <script>
        import { defineComponent } from "vue";
        export default defineComponent({
          name: "PayButton",
          props: { label: { type: String, default: "Pay" } },
          inject: ["cart"],
          data() {
            return { msg: "hello", count: 0 };
          },
          computed: {
            total() { return this.cart.sum(); },
          },
          watch: {
            count(next) { this.save(next); },
          },
          methods: {
            pay() { this.save(1); },
            save: function (amount) { return amount; },
          },
          mounted() { this.pay(); },
        });
        </script>
        """;

    private const string Module = """
        <template>
          <div>
            <p>{{ total }}</p>
          </div>
        </template>

        <script setup>
        import { track } from "./track.js";
        track("cart-view");
        const total = 4;
        </script>
        """;

    /// <inheritdoc/>
    public override IReadOnlyList<CodeGraphFixture> Fixtures { get; } =
    [
        new("src/Options.vue", Options)
        {
            Nodes = [new("vue:src/Options.vue", CodeNodeKinds.Type, (1, 10)), new("vue:src/Options.vue.msg", CodeNodeKinds.Member, (8, 8))],
            Edges = [new("vue:src/Options.vue", "vue:src/Options.vue.msg", CodeEdgeKinds.Uses, 2, CodeConfidences.Exact)],
        },
        new("src/Pay.vue", Pay)
        {
            Nodes =
            [
                new("vue:src/Pay.vue", CodeNodeKinds.Type, (1, 25)),
                new("vue:src/Pay.vue.data", CodeNodeKinds.Member, (10, 12)),
                new("vue:src/Pay.vue.pay", CodeNodeKinds.Member, (20, 20)),
            ],
            Edges =
            [
                new("vue:src/Pay.vue", "vue:src/Pay.vue.pay", CodeEdgeKinds.Calls, 2, CodeConfidences.Exact),
                new("vue:src/Pay.vue", "vue:src/Pay.vue.total", CodeEdgeKinds.Uses, 2, CodeConfidences.Exact),
                new("vue:src/Pay.vue", "vue:src/Pay.vue.label", CodeEdgeKinds.Uses, 2, CodeConfidences.Exact),
                new("vue:src/Pay.vue", "vue:src/Pay.vue.count", CodeEdgeKinds.Uses, 17, CodeConfidences.Exact),
                new("vue:src/Pay.vue.pay", "vue:src/Pay.vue.save", CodeEdgeKinds.Calls, 20, CodeConfidences.Exact),
                new("vue:src/Pay.vue.mounted", "vue:src/Pay.vue.pay", CodeEdgeKinds.Calls, 23, CodeConfidences.Exact),
            ],
        },
        new("src/Module.vue", Module)
        {
            Nodes = [new("vue:src/Module.vue", CodeNodeKinds.Type, (1, 11)), new("vue:src/Module.vue.(module)", CodeNodeKinds.Member, (9, 9))],
        },
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
    /// its template's component tags are instantiates and its classes and ids uses of the rules that style them, and its
    /// code is read by the JavaScript reader as Vue compiles it - a handler that names one a call of it, a binding and a
    /// <c>{{ }}</c> what their expressions use, each name read by itself a member of the component; its style block is
    /// read by the style sheet's reader.
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
        Assert.Contains(new LiteralRow("done", 14, "vue:src/Cart.vue.pay"), LiteralRow.Of(extract));

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
                new ExtractReference("vue:src/Cart.vue", CodeEdgeKinds.Uses, "name", 4) { Qualifier = new ExtractQualifier(CodeQualifierKinds.Value, "item") },
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
    /// one call, a modifier and a dashed binding are read, a bound class's object is what its values use and no selector, a
    /// data attribute none, and what a comment holds is no element.
    /// </summary>
    [Fact]
    public void ANestedTemplateEndsNothing_AndEachAttributeIsReadByItsName()
    {
        var extract = Read("src/Panel.vue", Panel);

        Assert.Equal(
            [
                ("isActive", CodeEdgeKinds.Uses, 12), ("ChildItem", CodeEdgeKinds.Instantiates, 13), ("ok", CodeEdgeKinds.Uses, 13), ("Other", CodeEdgeKinds.Instantiates, 14),
                ("save", CodeEdgeKinds.Calls, 14), ("Comp", CodeEdgeKinds.Instantiates, 15), ("count", CodeEdgeKinds.Uses, 15), ("submit", CodeEdgeKinds.Calls, 15),
            ],
            extract.References.Where(reference => reference.Line is >= 11 and <= 18).OrderBy(reference => reference.Line).Select(reference => (reference.Name, reference.Kind, reference.Line)));
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
            [
                new CodeEdge("vue:src/Cart.vue.pay", "vue:src/Billing.vue", CodeEdgeKinds.Uses, 14, CodeConfidences.Exact),
                new CodeEdge("vue:src/Cart.vue.pay", "vue:src/Billing.vue.charge", CodeEdgeKinds.Calls, 14, CodeConfidences.Exact),
            ],
            linked.Edges.Where(edge => edge.From == "vue:src/Cart.vue.pay" && edge.To.StartsWith("vue:src/Billing.vue", StringComparison.Ordinal)));
        Assert.Equal(
            [new CodeEdge("vue:src/Shop.vue", "vue:src/Billing.vue", CodeEdgeKinds.Uses, 3, CodeConfidences.Exact)],
            linked.Edges.Where(edge => edge.From.StartsWith("vue:src/Shop.vue", StringComparison.Ordinal) && edge.Kind == CodeEdgeKinds.Uses));
    }

    /// <summary>
    /// A literal the options object a component's script exports by default holds is held by the component, the node the
    /// object is folded into: the extract names no node it lacks, so the file is no unreadable one.
    /// </summary>
    [Fact]
    public void AnOptionsObjectsLiteral_IsHeldByTheComponent()
    {
        var input = new CodeFileInput("src/Options.vue", Options);
        var extract = new VueExtractor().Extract(input);

        var name = Assert.Single(LiteralRow.Of(extract), literal => literal.Value == "CartView");

        Assert.Null(extract.WithDigests(input.Lines).Problem("vue", input.Lines.Count));
        Assert.Equal(("vue:src/Options.vue", 6), (name.Node, name.Line));
    }

    /// <summary>
    /// A component written with the options API declares its members in its options: each prop and injection, each
    /// method and computed property, each function it holds - <c>data</c> and a lifecycle hook among them - and each
    /// property <c>data()</c> returns is a member of the component on its own lines, so its template binds to them and a
    /// method's <c>this.save()</c> to the method; a <c>watch</c> key is a use of the member it watches, and its handler's
    /// code the component's.
    /// </summary>
    [Fact]
    public void AnOptionsObjectsGroups_AreTheComponentsMembers_AndTheTemplateBindsToThem()
    {
        var extract = Read("src/Pay.vue", Pay);

        Assert.Equal(
            [
                ("label", "property", 8, 8), ("cart", "property", 9, 9), ("data", "method", 10, 12), ("msg", "field", 11, 11), ("count", "field", 11, 11),
                ("total", "computed", 14, 14), ("pay", "method", 20, 20), ("save", "method", 21, 21), ("mounted", "method", 23, 23),
            ],
            extract.Nodes.Where(node => node.Kind == CodeNodeKinds.Member).Select(node => (node.Name, node.SubKind!, node.Start, node.End)));
        Assert.All(extract.Nodes.Where(node => node.Kind == CodeNodeKinds.Member), node => Assert.Equal(("vue:src/Pay.vue", "src/Pay.vue"), (node.Parent, node.Scope)));

        var linked = CodeGraphLinker.Link([new CodeGraphFile("src/Pay.vue", "vue", extract)]);

        Assert.Equal(
            [
                new CodeEdge("vue:src/Pay.vue", "vue:src/Pay.vue.pay", CodeEdgeKinds.Calls, 2, CodeConfidences.Exact),
                new CodeEdge("vue:src/Pay.vue", "vue:src/Pay.vue.total", CodeEdgeKinds.Uses, 2, CodeConfidences.Exact),
                new CodeEdge("vue:src/Pay.vue", "vue:src/Pay.vue.msg", CodeEdgeKinds.Uses, 2, CodeConfidences.Exact),
                new CodeEdge("vue:src/Pay.vue", "vue:src/Pay.vue.label", CodeEdgeKinds.Uses, 2, CodeConfidences.Exact),
                new CodeEdge("vue:src/Pay.vue.total", "vue:src/Pay.vue.cart", CodeEdgeKinds.Uses, 14, CodeConfidences.Exact),
                new CodeEdge("vue:src/Pay.vue", "vue:src/Pay.vue.count", CodeEdgeKinds.Uses, 17, CodeConfidences.Exact),
                new CodeEdge("vue:src/Pay.vue", "vue:src/Pay.vue.save", CodeEdgeKinds.Calls, 17, CodeConfidences.Exact),
                new CodeEdge("vue:src/Pay.vue.pay", "vue:src/Pay.vue.save", CodeEdgeKinds.Calls, 20, CodeConfidences.Exact),
                new CodeEdge("vue:src/Pay.vue.mounted", "vue:src/Pay.vue.pay", CodeEdgeKinds.Calls, 23, CodeConfidences.Exact),
            ],
            linked.Edges.Where(edge => edge.Kind != CodeEdgeKinds.Contains && edge.To.StartsWith("vue:src/Pay.vue.", StringComparison.Ordinal)).OrderBy(edge => edge.Line));
        Assert.Equal(
            [("PayButton", "vue:src/Pay.vue"), ("Pay", "vue:src/Pay.vue.label"), ("cart", "vue:src/Pay.vue"), ("hello", "vue:src/Pay.vue.data")],
            LiteralRow.Of(extract).Select(literal => (literal.Value, literal.Node)));
    }

    /// <summary>
    /// The props a setup script's <c>defineProps</c> declares are members of the component, so its template binds to them:
    /// those of an interface the script declares and of a type written in the call alike, an array's strings and an
    /// object's keys too.
    /// </summary>
    [Fact]
    public void ThePropsDefinePropsDeclares_AreTheComponentsMembers()
    {
        var extract = Read("src/Props.vue", """
            <script setup lang="ts">
            interface Props {
              title: string;
              readonly count?: number
            }
            const props = defineProps<Props>();
            const other = withDefaults(defineProps<{ label: string; size?: number }>(), { size: 1 });
            </script>
            <script>
            const listed = defineProps(["first", "second"]);
            const keyed = defineProps({ third: String });
            </script>
            <template>
              <h1>{{ title }} {{ count }} {{ label }} {{ size }}</h1>
            </template>
            """);

        Assert.Equal(
            [("title", 3), ("count", 4), ("label", 7), ("size", 7), ("first", 10), ("second", 10), ("third", 11)],
            extract.Nodes.Where(node => node.SubKind == "property" && node.Parent is null).Select(node => (node.Name, node.Start)));

        var linked = CodeGraphLinker.Link([new CodeGraphFile("src/Props.vue", "vue", extract)]);

        Assert.Equal(
            ["vue:src/Props.vue.title", "vue:src/Props.vue.count", "vue:src/Props.vue.label", "vue:src/Props.vue.size"],
            linked.Edges.Where(edge => edge.From == "vue:src/Props.vue" && edge.Line == 14).Select(edge => edge.To));
    }

    /// <summary>
    /// What a component's script does at its top outside its declarations is over the lines that code is written on, not
    /// from the file's first line: its body is the script's, never the template before it.
    /// </summary>
    [Fact]
    public void AScriptsTop_IsOverTheLinesItsCodeIsWrittenOn()
    {
        var top = Assert.Single(Read("src/Module.vue", Module).Nodes, node => node.Name == "(module)");

        Assert.Equal(("vue:src/Module.vue.(module)", 9, 9), (top.Id, top.Start, top.End));
    }

    /// <summary>
    /// A component's tag named as an element no end tag closes - a <c>Link</c>, a <c>Col</c> - is matched as written, so
    /// it holds what it is written around and the names its loop and its slot declare are its content's own; an
    /// <c>input</c> as written is that element, and holds nothing.
    /// </summary>
    [Fact]
    public void AComponentNamedAsAVoidElement_HoldsItsContent()
    {
        var extract = Read("src/Nav.vue", """
            <template>
              <nav>
                <Link v-for="link in links" :href="link.url">{{ link.label }}</Link>
                <Col v-slot="{ row }">{{ row.name }}</Col>
                <input v-model="query">
                <p>{{ link }}</p>
              </nav>
            </template>
            """);

        Assert.Equal(
            [
                ("Link", CodeEdgeKinds.Instantiates, 3, (string?)null), ("links", CodeEdgeKinds.Uses, 3, CodeQualifierKinds.This), ("url", CodeEdgeKinds.Uses, 3, CodeQualifierKinds.Value),
                ("label", CodeEdgeKinds.Uses, 3, CodeQualifierKinds.Value), ("Col", CodeEdgeKinds.Instantiates, 4, null), ("name", CodeEdgeKinds.Uses, 4, CodeQualifierKinds.Value),
                ("query", CodeEdgeKinds.Uses, 5, CodeQualifierKinds.This), ("link", CodeEdgeKinds.Uses, 6, CodeQualifierKinds.This),
            ],
            extract.References.OrderBy(reference => reference.Line).Select(reference => (reference.Name, reference.Kind, reference.Line, reference.Qualifier?.Kind)));
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
    /// component's own - is what its expression uses, and a <c>v-for</c> what the list it walks does; a name a
    /// <c>v-for</c>'s aliases or a slot's value declares is the template's own where it stands - in the element and what
    /// it holds, its text among it, and nowhere after its end - and no member's, a member of it a member of that value,
    /// though a property a slot's pattern renames, and a name its default value gives, is a member of the component.
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
                ("items", CodeEdgeKinds.Uses, 3, CodeQualifierKinds.This), ("pick", CodeEdgeKinds.Calls, 3, CodeQualifierKinds.This), ("name", CodeEdgeKinds.Uses, 3, CodeQualifierKinds.Value),
                ("total", CodeEdgeKinds.Uses, 3, CodeQualifierKinds.This), ("name", CodeEdgeKinds.Uses, 5, CodeQualifierKinds.This), ("shown", CodeEdgeKinds.Uses, 5, CodeQualifierKinds.This),
                ("target", CodeEdgeKinds.Uses, 5, CodeQualifierKinds.This), ("Table", CodeEdgeKinds.Instantiates, 6, (string?)null), ("fallback", CodeEdgeKinds.Uses, 6, CodeQualifierKinds.This),
                ("row", CodeEdgeKinds.Uses, 6, CodeQualifierKinds.This), ("fallback", CodeEdgeKinds.Uses, 6, CodeQualifierKinds.This), ("Panel", CodeEdgeKinds.Instantiates, 7, null),
                ("title", CodeEdgeKinds.Uses, 7, CodeQualifierKinds.Value), ("item", CodeEdgeKinds.Uses, 8, CodeQualifierKinds.This), ("Panel", CodeEdgeKinds.Instantiates, 9, null),
                ("note", CodeEdgeKinds.Uses, 9, CodeQualifierKinds.This),
            ],
            extract.References.OrderBy(reference => reference.Line).Select(reference => (reference.Name, reference.Kind, reference.Line, reference.Qualifier?.Kind)));
    }

    /// <summary>
    /// A handler's value is compiled as Vue's compiler compiles it and read by the JavaScript reader: a member path - a
    /// name, <c>cart.add</c> - a call of the handler it names with whatever the event gives, of no count of arguments; a
    /// function expression the handler itself; anything else statements the event runs - an <c>if</c> no name -
    /// <c>$event</c> its own. A directive's dynamic argument is an expression of its own, read in its own case, and a
    /// global Vue's compiler leaves to itself - <c>Math</c> - is no member.
    /// </summary>
    [Fact]
    public void AHandlersValue_IsCompiledAsVueCompilesIt_AndReadByTheJavaScriptReader()
    {
        var extract = Read("src/Act.vue", """
            <template>
              <button @click="pay" @focus="cart.add" @blur="(e) => track(e, 1)" @keyup="if (ok) send($event); else stop()">x</button>
              <Comp :[slotKey]="Math.max(a, 1)" @[event]="handle" />
              <p>{{ isNaN(count) }}</p>
            </template>
            """);

        Assert.Equal(
            [
                ("Comp", CodeEdgeKinds.Instantiates, 3, (int?)null, (string?)null, (string?)null), ("pay", CodeEdgeKinds.Calls, 2, null, CodeQualifierKinds.This, null),
                ("cart", CodeEdgeKinds.Uses, 2, null, CodeQualifierKinds.This, null), ("add", CodeEdgeKinds.Calls, 2, null, CodeQualifierKinds.Value, "cart"),
                ("track", CodeEdgeKinds.Calls, 2, 2, CodeQualifierKinds.This, null), ("ok", CodeEdgeKinds.Uses, 2, null, CodeQualifierKinds.This, null),
                ("send", CodeEdgeKinds.Calls, 2, 1, CodeQualifierKinds.This, null), ("stop", CodeEdgeKinds.Calls, 2, 0, CodeQualifierKinds.This, null),
                ("slotKey", CodeEdgeKinds.Uses, 3, null, CodeQualifierKinds.This, null), ("max", CodeEdgeKinds.Calls, 3, 2, CodeQualifierKinds.Named, "Math"),
                ("a", CodeEdgeKinds.Uses, 3, null, CodeQualifierKinds.This, null), ("event", CodeEdgeKinds.Uses, 3, null, CodeQualifierKinds.This, null),
                ("handle", CodeEdgeKinds.Calls, 3, null, CodeQualifierKinds.This, null), ("isNaN", CodeEdgeKinds.Calls, 4, 1, null, null),
                ("count", CodeEdgeKinds.Uses, 4, null, CodeQualifierKinds.This, null),
            ],
            extract.References.Select(reference => (reference.Name, reference.Kind, reference.Line, reference.Arity, reference.Qualifier?.Kind, reference.Qualifier?.Name)));
        Assert.Equal(["vue:src/Act.vue"], extract.Nodes.Select(node => node.Id));
    }

    /// <summary>
    /// A block that names its file by <c>src</c> - a template's, a script's, a style's - is an import of that file, as a
    /// page's <c>src</c> is, by the one reading of what a <c>src</c> names: another site's names none.
    /// </summary>
    [Fact]
    public void ABlockThatNamesItsFileBySrc_IsAnImportOfIt()
    {
        var extract = Read("src/Act.vue", "<template src=\"./act.html\"></template>\n<script src=\"./act.js?v=2\"></script>\n<style src=\"../css/act.css\"></style>\n<script src=\"https://cdn.example/x.js\"></script>\n");

        Assert.Equal(
            [(CodeImportForms.Relative, "act.html", 1), (CodeImportForms.Relative, "act.js", 2), (CodeImportForms.Relative, "../css/act.css", 3)],
            extract.Imports.Select(import => (import.Form, import.Target, import.Line)));
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

    /// <summary>
    /// A style block is read in the syntax its <c>lang</c> names: a SCSS block's <c>//</c> comment is no rule, its
    /// <c>$radius</c> a variable, and the rule that writes it a use of it, so the template's class reaches the rule and the
    /// rule the variable; a block of a syntax no style sheet's reader reads is not read.
    /// </summary>
    [Fact]
    public void AStyleBlock_IsReadInTheSyntaxItsLangNames()
    {
        var extract = Read("src/Card.vue", "<template>\n  <div class=\"card\"></div>\n</template>\n<style lang=\"scss\" scoped>\n// .ghost { a: b; }\n$radius: 4px;\n.card { border-radius: $radius; }\n</style>\n<style lang=\"stylus\">\n.plain\n  a b\n</style>\n");

        Assert.Equal(
            [("$radius", CodeSubKinds.StyleVariable, 6), (".card", CodeSubKinds.Rule, 7)],
            extract.Nodes.Where(node => CodeSubKinds.IsData(node.SubKind)).Select(node => (node.Name, node.SubKind!, node.Start)));

        var linked = CodeGraphLinker.Link([new CodeGraphFile("src/Card.vue", "vue", extract)]);

        Assert.Equal(
            [
                new CodeEdge("vue:src/Card.vue", "vue:src/Card.vue#.card", CodeEdgeKinds.Uses, 2, CodeConfidences.Exact),
                new CodeEdge("vue:src/Card.vue#.card", "vue:src/Card.vue#$radius", CodeEdgeKinds.Uses, 7, CodeConfidences.Exact),
            ],
            linked.Edges.Where(edge => edge.Kind == CodeEdgeKinds.Uses).OrderBy(edge => edge.Line));
    }

    /// <summary>
    /// A component's <c>&lt;style module&gt;</c> is a CSS module its binding reads: <c>$style.red</c> in the template and
    /// <c>this.$style.red</c> in a method use the module's <c>.red</c>, and <c>classes.card__title</c> of a block bound as
    /// <c>classes</c> the suffix its <c>&amp;__title</c> writes onto <c>.card</c> and <c>.tile</c>, each exact through the
    /// module's exports; the template's own <c>class="red"</c> reaches none of the module's - which the module renames -
    /// but the <c>&lt;style scoped&gt;</c> rule of that name, the component's own, which no other file's class reaches.
    /// </summary>
    [Fact]
    public void AStyleModule_IsReachedThroughItsBindingAlone_AndAScopedBlockFromItsComponentAlone()
    {
        var extract = Read("src/Tag.vue", """
            <template>
              <p :class="$style.red" class="red tint"></p>
              <h2 :class="classes.card__title"></h2>
            </template>
            <script>
            export default {
              methods: {
                paint() { return this.$style.red; },
              },
            };
            </script>
            <style module>
            .red { color: red; }
            </style>
            <style module="classes" lang="scss">
            .card, .tile { &__title { margin: 0; } }
            </style>
            <style scoped>
            .red { color: crimson; }
            .tint { opacity: 0.5; }
            </style>
            """);

        // Each member a binding reads names the scope its block keeps its own names to.
        Assert.Equal(
            [
                new ExtractReference("vue:src/Tag.vue", CodeEdgeKinds.Uses, "red", 2) { Qualifier = new ExtractQualifier(CodeQualifierKinds.Module, "src/Tag.vue#$style") },
                new ExtractReference("vue:src/Tag.vue", CodeEdgeKinds.Uses, "card__title", 3) { Qualifier = new ExtractQualifier(CodeQualifierKinds.Module, "src/Tag.vue#classes") },
                new ExtractReference("vue:src/Tag.vue.paint", CodeEdgeKinds.Uses, "red", 8) { Qualifier = new ExtractQualifier(CodeQualifierKinds.Module, "src/Tag.vue#$style") },
            ],
            extract.References.Where(reference => reference.Qualifier?.Kind == CodeQualifierKinds.Module));

        var linked = CodeGraphLinker.Link(
        [
            new CodeGraphFile("src/Tag.vue", "vue", extract),
            new CodeGraphFile("src/other.html", "html", new HtmlExtractor().Extract(new CodeFileInput("src/other.html", "<p class=\"tint red\"></p>\n"))),
        ]);

        Assert.Equal(
            [
                new CodeEdge("vue:src/Tag.vue", "vue:src/Tag.vue#.red", CodeEdgeKinds.Uses, 2, CodeConfidences.Exact),
                new CodeEdge("vue:src/Tag.vue", "vue:src/Tag.vue#.red~2", CodeEdgeKinds.Uses, 2, CodeConfidences.Exact),
                new CodeEdge("vue:src/Tag.vue", "vue:src/Tag.vue#.tint", CodeEdgeKinds.Uses, 2, CodeConfidences.Exact),
                new CodeEdge("vue:src/Tag.vue", "vue:src/Tag.vue#.card&__title", CodeEdgeKinds.Uses, 3, CodeConfidences.Exact),
                new CodeEdge("vue:src/Tag.vue.paint", "vue:src/Tag.vue#.red", CodeEdgeKinds.Uses, 8, CodeConfidences.Exact),
            ],
            linked.Edges.Where(edge => edge.Kind == CodeEdgeKinds.Uses && edge.To.Contains('#', StringComparison.Ordinal))
                .OrderBy(edge => edge.Line).ThenBy(edge => edge.To, StringComparer.Ordinal));
    }

    private static FileExtract Read(string path, string text) => new VueExtractor().Extract(new CodeFileInput(path, text));
}
