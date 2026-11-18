using DssHarness.Core.Codebase;
using DssHarness.Core.Codebase.Languages;

namespace DssHarness.Tests;

/// <summary>A Razor view and a Blazor component, held to the contract's kit, and the rules of their own the kit does not name.</summary>
public sealed class RazorExtractorTests : CodeGraphExtractorContractTests<RazorExtractor>
{
    private const string View = """
        @page "/cart"
        @using Shop.Web
        @model CartModel
        @inject CartService Carts
        <h1>Cart</h1>
        <partial name="_Total" />
        @Html.Partial("_Rows")
        @functions {
            public string Label()
            {
                return "rows";
            }
        }
        """;

    private const string Component = """
        @namespace Shop.Web
        @inject CartService Carts
        <MyButton Text="Go" @onclick="Pay" @bind="Total" />
        @code {
            private int count;

            [Parameter]
            public int Count
            {
                get => count;
                set => count = value;
            }

            private void Pay()
            {
                Carts.Add(Count);
            }
        }
        """;

    private const string ViewImports = """
        @using Shop.Web
        @using Shop.Web.Models
        """;

    /// <inheritdoc/>
    public override IReadOnlyList<CodeGraphFixture> Fixtures { get; } =
    [
        new("Views/Cart.cshtml", View)
        {
            Nodes =
            [
                new("razor:Views/Cart.cshtml#view", CodeNodeKinds.Section, (1, 13)),
                new("razor:Views/Cart.cshtml#Carts", CodeNodeKinds.Member, (4, 4)),
                new("razor:Views.Cart.Label()", CodeNodeKinds.Member),
            ],
        },
        new("Shared/Cart.razor", Component)
        {
            Nodes =
            [
                new("razor:Shop.Web.Cart", CodeNodeKinds.Type, (1, 18)),
                new("razor:Shop.Web.Cart.Count", CodeNodeKinds.Member),
                new("razor:Shop.Web.Cart.Pay()", CodeNodeKinds.Member),
            ],
        },
        new("Views/_ViewImports.cshtml", ViewImports),
        new("Pages/_Imports.razor", "@using Shop.Web\n"),
    ];

    /// <summary>
    /// A view is a section over its lines; its <c>@model</c> a reference and its <c>@inject</c> a member with the type
    /// it is given; its <c>@functions</c> code is the class the page compiles to, read by the C# reader with the markup
    /// blanked, each declaration on the line it stands on; a <c>&lt;partial name=</c> and an <c>@Html.Partial</c> are
    /// instantiates of the views they name.
    /// </summary>
    [Fact]
    public void AViewsDirectivesAreRead_AndItsFunctionsCodeCarriesItsLines()
    {
        var extract = Read("Views/Cart.cshtml", View);

        Assert.Equal(["view"], extract.Nodes.Where(node => node.Kind == CodeNodeKinds.Section).Select(node => node.SubKind));

        var injected = Assert.Single(extract.Nodes, node => node.Id == "razor:Views/Cart.cshtml#Carts");

        Assert.Equal(("Carts", "CartService"), (injected.Name, injected.ValueType));

        var label = Assert.Single(extract.Nodes, node => node.Name == "Label");

        Assert.Equal((9, 12), (label.Start, label.End));
        Assert.Equal("razor:Views.Cart.Label()", label.Id);
        Assert.Equal([("CartModel", CodeEdgeKinds.References, 3), ("CartService", CodeEdgeKinds.References, 4), ("_Total", CodeEdgeKinds.Instantiates, 6), ("_Rows", CodeEdgeKinds.Instantiates, 7)],
            extract.References.Where(reference => reference.Kind != CodeEdgeKinds.Calls && reference.Kind != CodeEdgeKinds.Uses).Select(reference => (reference.Name, reference.Kind, reference.Line)));
        Assert.Equal([(CodeImportForms.Scope, "Shop.Web", 2)], extract.Imports.Select(import => (import.Form, import.Target, import.Line)));
        Assert.Contains(new ExtractLiteral("rows", 11), extract.Literals ?? []);
    }

    /// <summary>
    /// A component is a type named by its file and its <c>@namespace</c>, a part of its code-behind partial - the class
    /// its <c>@code</c> block is, read by the C# reader with the markup blanked; a PascalCase element an instantiates,
    /// an <c>@on*</c> attribute a call and another <c>@</c> attribute a use.
    /// </summary>
    [Fact]
    public void AComponentIsATypePartOfItsCodeBehind_AndItsMarkupRead()
    {
        var extract = Read("Shared/Cart.razor", Component);
        var component = Assert.Single(extract.Nodes, node => node.Id == "razor:Shop.Web.Cart");

        Assert.Equal(("Shop.Web.Cart", "class", true, (1, 18)), (component.Qualified, component.SubKind, component.Part, (component.Start, component.End)));

        var pay = Assert.Single(extract.Nodes, node => node.Id == "razor:Shop.Web.Cart.Pay()");

        Assert.Equal((14, 17), (pay.Start, pay.End));
        Assert.Equal("razor:Shop.Web.Cart", Assert.Single(extract.Nodes, node => node.Id == "razor:Shop.Web.Cart.Count").Parent);
        Assert.Equal([("MyButton", CodeEdgeKinds.Instantiates, 3), ("Pay", CodeEdgeKinds.Calls, 3), ("Total", CodeEdgeKinds.Uses, 3)],
            extract.References.Where(reference => reference.Line == 3).Select(reference => (reference.Name, reference.Kind, reference.Line)));
    }

    /// <summary>
    /// A <c>_ViewImports.cshtml</c> and an <c>_Imports.razor</c> declare each <c>@using</c> they hold for the whole of
    /// their directory's subtree: an import of the directory form, which the linker applies to every file of the
    /// language under it.
    /// </summary>
    [Fact]
    public void AViewImportsAndItsUsing_AreImportsOfTheDirectoryForm()
    {
        var extract = Read("Views/_ViewImports.cshtml", ViewImports);

        Assert.Equal([(CodeImportForms.Directory, "Shop.Web", 1), (CodeImportForms.Directory, "Shop.Web.Models", 2)],
            extract.Imports.Select(import => (import.Form, import.Target, import.Line)));
    }

    /// <summary>A view and its code-behind partial are joined by partOf, walked as one.</summary>
    [Fact]
    public void AComponentAndItsCodeBehind_AreJoinedByPartOf()
    {
        var linked = CodeGraphLinker.Link(
        [
            new CodeGraphFile("Shared/Cart.razor", "razor", new RazorExtractor().Extract(new CodeFileInput("Shared/Cart.razor", Component))),
            new CodeGraphFile("Shared/Cart.razor.cs", "csharp", new CSharpExtractor().Extract(new CodeFileInput("Shared/Cart.razor.cs", """
                namespace Shop.Web
                {
                    public partial class Cart
                    {
                        private void Built()
                        {
                        }
                    }
                }
                """))),
        ]);

        Assert.Contains(new CodeEdge("razor:Shop.Web.Cart", "csharp:Shop.Web.Cart", CodeEdgeKinds.PartOf, 1, CodeConfidences.Exact), linked.Edges);
    }

    private static FileExtract Read(string path, string text) => new RazorExtractor().Extract(new CodeFileInput(path, text));
}
