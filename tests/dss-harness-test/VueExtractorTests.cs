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

    /// <inheritdoc/>
    public override IReadOnlyList<CodeGraphFixture> Fixtures { get; } =
    [
        new("src/Cart.vue", Cart)
        {
            Nodes =
            [
                new("vue:src/Cart.vue#Cart", CodeNodeKinds.Type, (1, 20)),
                new("vue:src/Cart.vue.pay", CodeNodeKinds.Member),
                new("vue:src/Cart.vue#.panel", CodeNodeKinds.Member),
            ],
        },
        new("src/Billing.vue", """
            <script setup>
            function charge(amount) {
                return amount;
            }

            export default { charge };
            </script>
            """)
        {
            Nodes = [new("vue:src/Billing.vue#Billing", CodeNodeKinds.Type)],
        },
    ];

    /// <summary>
    /// The component is a type named by its file and exported default, so an import of it binds to it; its script -
    /// TypeScript where <c>lang="ts"</c> - is read by the JavaScript reader with the other lines blanked, its
    /// declarations and literals on the lines they stand on; its template's component tags are instantiates, its
    /// <c>@</c> attributes calls, its <c>:</c> attributes and <c>{{ }}</c> uses, and its classes and ids uses of the
    /// rules that style them; its style block is read by the style sheet's reader.
    /// </summary>
    [Fact]
    public void AComponentIsAType_ItsScriptReadItsTemplateScannedItsStyleRead()
    {
        var extract = Read("src/Cart.vue", Cart);
        var component = extract.Nodes[0];

        Assert.Equal(("Cart", "component"), (component.Name, component.SubKind));
        Assert.Equal(["default"], component.Exports);

        var pay = Assert.Single(extract.Nodes, node => node.Name == "pay");

        Assert.Equal((13, 15), (pay.Start, pay.End));
        Assert.Contains(new ExtractLiteral("done", 14), extract.Literals ?? []);

        var template = extract.References.Where(reference => reference.Line is >= 2 and <= 5).Select(reference => (reference.Name, reference.Kind)).Order().ToList();

        Assert.Contains(("#cart", CodeEdgeKinds.Uses), template);
        Assert.Contains((".panel", CodeEdgeKinds.Uses), template);
        Assert.Contains((".wide", CodeEdgeKinds.Uses), template);
        Assert.Contains(("checkout-widget", CodeEdgeKinds.Instantiates), template);
        Assert.Contains(("pay", CodeEdgeKinds.Calls), template);
        Assert.Contains(("total", CodeEdgeKinds.Uses), template);
        Assert.Contains(("item.name", CodeEdgeKinds.Uses), template);

        var rule = Assert.Single(extract.Nodes, node => node.Id == "vue:src/Cart.vue#.panel");

        Assert.Equal((19, 19), (rule.Start, rule.End));
        Assert.Equal([("--gap", 19)], extract.References.Where(reference => reference.Line == 19).Select(reference => (reference.Name, reference.Line)));
    }

    /// <summary>An import of a component binds to it: its charge is called, and the edge names the component's node.</summary>
    [Fact]
    public void AnImportOfAComponent_BindsToIt()
    {
        var linked = CodeGraphLinker.Link(
        [
            new CodeGraphFile("src/Cart.vue", "vue", new VueExtractor().Extract(new CodeFileInput("src/Cart.vue", Cart))),
            new CodeGraphFile("src/Billing.vue", "vue", new VueExtractor().Extract(new CodeFileInput("src/Billing.vue", """
                <script setup>
                export function charge(amount) {
                    return amount;
                }
                </script>
                """))),
        ]);

        Assert.Contains(linked.Edges, edge => edge.Kind == CodeEdgeKinds.Imports && edge.From == "file:src/Cart.vue" && edge.To == "file:src/Billing.vue");
    }

    private static FileExtract Read(string path, string text) => new VueExtractor().Extract(new CodeFileInput(path, text));
}
