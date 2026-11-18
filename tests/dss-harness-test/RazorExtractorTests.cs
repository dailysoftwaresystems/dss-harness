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

    private const string Braces = """
        @using static System.Math
        @using Alias = Some.Namespace
        @inject ILogger<Index> Logger
        @* @inject IOld Old <OldWidget /> *@
        <Header @bind-Value="Model.Name" @bind-Value:event="oninput" @onclick:preventDefault @onclick="() => Select(item)" />
        @code
        {
            private string open = "{";
            private string close = "}"; // }

            private void Select(string item)
            {
                var items = new List<Item>();
            }
        }
        <Footer />
        @Html.Partial("_Item", item)
        @await Html.PartialAsync("_Other")
        @code { [Parameter] public string Title { get; set; } }
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
                new("razor:Views.Cart", CodeNodeKinds.Type, (1, 13)),
                new("razor:Views.Cart.Carts", CodeNodeKinds.Member, (4, 4)),
                new("razor:Views.Cart.Label()", CodeNodeKinds.Member, (9, 12)),
            ],
        },
        new("Shared/Cart.razor", Component)
        {
            Nodes =
            [
                new("razor:Shop.Web.Cart", CodeNodeKinds.Type, (1, 18)),
                new("razor:Shop.Web.Cart.Count", CodeNodeKinds.Member),
                new("razor:Shop.Web.Cart.Pay()", CodeNodeKinds.Member, (14, 17)),
            ],
            Edges =
            [
                new("razor:Shop.Web.Cart", "razor:Shop.Web.Cart.Pay()", CodeEdgeKinds.Calls, 3, CodeConfidences.Exact),
                new("razor:Shop.Web.Cart.Pay()", "razor:Shop.Web.Cart.Count", CodeEdgeKinds.Uses, 16, CodeConfidences.Exact),
            ],
        },
        new("src/my-app/Pages/Index.razor", Braces)
        {
            Nodes =
            [
                new("razor:src.my_app.Pages.Index", CodeNodeKinds.Type, (1, 19)),
                new("razor:src.my_app.Pages.Index.Select(string)", CodeNodeKinds.Member, (11, 14)),
                new("razor:src.my_app.Pages.Index.Title", CodeNodeKinds.Member, (19, 19)),
            ],
        },
        new("App.razor", "@code {\n    private void Start()\n    {\n    }\n}\n")
        {
            Nodes = [new("razor:App", CodeNodeKinds.Type, (1, 5)), new("razor:App.Start()", CodeNodeKinds.Member, (2, 4))],
        },
        new("Views/_ViewImports.cshtml", ViewImports),
        new("Pages/_Imports.razor", "@using Shop.Web\n"),
    ];

    /// <summary>
    /// A view is a section over its lines and the class it compiles to is read by the C# reader with the markup blanked:
    /// its <c>@model</c> the <c>Model</c> property, its <c>@inject</c> a property with the type it is given, its
    /// <c>@functions</c> code the class's own, each declaration on the line it stands on; a <c>&lt;partial name=</c> and an
    /// <c>@Html.Partial</c> are instantiates of the views they name.
    /// </summary>
    [Fact]
    public void AViewsDirectivesAreRead_AndItsFunctionsCodeCarriesItsLines()
    {
        var extract = Read("Views/Cart.cshtml", View);

        Assert.Equal(["view"], extract.Nodes.Where(node => node.Kind == CodeNodeKinds.Section).Select(node => node.SubKind));
        Assert.Equal(
            [("razor:Views.Cart.Model", "CartModel", 3), ("razor:Views.Cart.Carts", "CartService", 4)],
            extract.Nodes.Where(node => node.SubKind == "property").Select(node => (node.Id, node.ValueType, node.Start)));

        var label = Assert.Single(extract.Nodes, node => node.Name == "Label");

        Assert.Equal(("razor:Views.Cart.Label()", 9, 12), (label.Id, label.Start, label.End));
        Assert.Equal(
            [("CartModel", CodeEdgeKinds.References, 3), ("CartService", CodeEdgeKinds.References, 4), ("_Total", CodeEdgeKinds.Instantiates, 6), ("_Rows", CodeEdgeKinds.Instantiates, 7)],
            extract.References.Where(reference => reference.Kind != CodeEdgeKinds.Calls && reference.Kind != CodeEdgeKinds.Uses).Select(reference => (reference.Name, reference.Kind, reference.Line)).OrderBy(reference => reference.Line));
        Assert.Equal([(CodeImportForms.Scope, "Shop.Web", 2)], extract.Imports.Select(import => (import.Form, import.Target, import.Line)));
        Assert.Contains(new ExtractLiteral("rows", 11) { Node = "razor:Views.Cart.Label()" }, extract.Literals ?? []);
    }

    /// <summary>
    /// A component is a type named by its file and its <c>@namespace</c>, a part of its code-behind partial - the class
    /// its <c>@code</c> block is, read by the C# reader with the markup blanked; a PascalCase element an instantiates, an
    /// <c>@on*</c> attribute a call of the component's member and another <c>@</c> attribute a use of one.
    /// </summary>
    [Fact]
    public void AComponentIsATypePartOfItsCodeBehind_AndItsMarkupRead()
    {
        var extract = Read("Shared/Cart.razor", Component);
        var component = Assert.Single(extract.Nodes, node => node.Kind == CodeNodeKinds.Type);

        Assert.Equal(("razor:Shop.Web.Cart", "Shop.Web.Cart", "component", true, (1, 18)), (component.Id, component.Qualified, component.SubKind, component.Part, (component.Start, component.End)));

        var pay = Assert.Single(extract.Nodes, node => node.Id == "razor:Shop.Web.Cart.Pay()");
        var members = new ExtractQualifier(CodeQualifierKinds.This);

        Assert.Equal((14, 17), (pay.Start, pay.End));
        Assert.Equal("razor:Shop.Web.Cart", Assert.Single(extract.Nodes, node => node.Id == "razor:Shop.Web.Cart.Count").Parent);
        Assert.Equal(
            [
                new ExtractReference("razor:Shop.Web.Cart", CodeEdgeKinds.Instantiates, "MyButton", 3),
                new ExtractReference("razor:Shop.Web.Cart", CodeEdgeKinds.Calls, "Pay", 3) { Qualifier = members },
                new ExtractReference("razor:Shop.Web.Cart", CodeEdgeKinds.Uses, "Total", 3) { Qualifier = members },
            ],
            extract.References.Where(reference => reference.Line == 3));
    }

    /// <summary>
    /// A code block ends at its own brace as C#'s lexer reads it - a brace a string, a character or a comment holds ends
    /// nothing, and one on the line after the keyword opens it - so the code after a string's brace is read and the markup
    /// after the block is markup; a block on one line keeps its members, and the C# a block holds is never read as markup.
    /// </summary>
    [Fact]
    public void ACodeBlockEndsAtItsOwnBrace_AsCSharpReadsIt()
    {
        var extract = Read("src/my-app/Pages/Index.razor", Braces);

        Assert.Equal(
            [("open", 8), ("close", 9), ("Select", 11), ("Title", 19)],
            extract.Nodes.Where(node => node.Kind == CodeNodeKinds.Member && node.SubKind is "field" or "method" or "property" && node.Name != "Logger").Select(node => (node.Name, node.Start)));
        Assert.Equal(
            [("Header", 5), ("List`1", 13), ("Footer", 16), ("_Item", 17), ("_Other", 18)],
            extract.References.Where(reference => reference.Kind == CodeEdgeKinds.Instantiates).Select(reference => (reference.Name, reference.Line)).OrderBy(reference => reference.Line));
    }

    /// <summary>
    /// A file whose first line opens its code keeps its class, and a component at the tree's root is in no namespace: one
    /// type, the class its code is, and no namespace node of its own name beside it.
    /// </summary>
    [Fact]
    public void AFileThatBeginsWithItsCode_AtTheRoot_IsOneType()
    {
        var extract = Read("App.razor", "@code {\n    private void Start()\n    {\n    }\n}\n");

        Assert.Equal([("razor:App", CodeNodeKinds.Type, 1, 5), ("razor:App.Start()", CodeNodeKinds.Member, 2, 4)], extract.Nodes.Select(node => (node.Id, node.Kind, node.Start, node.End)));
    }

    /// <summary>
    /// The namespace a component's directory gives is made of C# names as Razor makes them, so the class the C# reader
    /// reads and the component are one node, its members in it.
    /// </summary>
    [Fact]
    public void AFoldersNamespace_IsMadeOfCSharpNames()
    {
        var extract = Read("src/my-app/Pages/Index.razor", Braces);

        Assert.Equal(["razor:src.my_app.Pages.Index"], extract.Nodes.Where(node => node.Kind == CodeNodeKinds.Type).Select(node => node.Id));
        Assert.Equal("razor:src.my_app.Pages.Index", Assert.Single(extract.Nodes, node => node.Name == "Select").Parent);
    }

    /// <summary>
    /// A <c>@using</c> is read by the C# reader's own rule - a static one imports its type, an alias names its prefix -
    /// and an <c>@inject</c> is a property of the type it is given, a generic one among them, its reference by the same rule.
    /// </summary>
    [Fact]
    public void UsingsAndInjects_AreReadByTheCSharpReadersRule()
    {
        var extract = Read("src/my-app/Pages/Index.razor", Braces);
        var logger = Assert.Single(extract.Nodes, node => node.Name == "Logger");

        Assert.Equal([("System.Math", null, 1), ("Some.Namespace", "Alias", 2)], extract.Imports.Select(import => (import.Target, import.Prefix, import.Line)));
        Assert.Equal(("property", "razor:src.my_app.Pages.Index", 3), (logger.SubKind, logger.Parent, logger.Start));
        Assert.Equal(["ILogger`1", "Index"], extract.References.Where(reference => reference.Kind == CodeEdgeKinds.References && reference.Line == 3).Select(reference => reference.Name).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// What a Razor comment holds is no directive and no markup, and a directive attribute is read whole by its name and
    /// its modifier: <c>@bind-Value</c> a use, an arrow function's call a call, and a modifier that holds no expression
    /// nothing.
    /// </summary>
    [Fact]
    public void ACommentsContentIsUnread_AndADirectiveAttributeIsReadWhole()
    {
        var extract = Read("src/my-app/Pages/Index.razor", Braces);
        var members = new ExtractQualifier(CodeQualifierKinds.This);

        Assert.DoesNotContain(extract.Nodes, node => node.Name == "Old");
        Assert.DoesNotContain(extract.References, reference => reference.Name is "IOld" or "OldWidget" or "Old");
        Assert.Equal(
            [
                new ExtractReference("razor:src.my_app.Pages.Index", CodeEdgeKinds.Instantiates, "Header", 5),
                new ExtractReference("razor:src.my_app.Pages.Index", CodeEdgeKinds.Uses, "Model", 5) { Qualifier = members },
                new ExtractReference("razor:src.my_app.Pages.Index", CodeEdgeKinds.Calls, "Select", 5) { Qualifier = members },
            ],
            extract.References.Where(reference => reference.Line == 5));
    }

    /// <summary>
    /// A binding's modifiers are read by what each says: <c>:after</c> and <c>:set</c> name a handler, a call of it;
    /// <c>:get</c> a value, a use of it; and one that holds no expression - <c>:format</c> - nothing, on a
    /// <c>@bind-Value</c> and a <c>@bind</c> alike.
    /// </summary>
    [Fact]
    public void ABindingsModifiers_AreReadByWhatEachSays()
    {
        var extract = Read("Shared/Field.razor", "<Input @bind-Value=\"Name\" @bind-Value:after=\"Saved\" @bind:get=\"Value\" @bind:set=\"Changed\" @bind:format=\"yyyy\" />\n");
        var members = new ExtractQualifier(CodeQualifierKinds.This);

        Assert.Equal(
            [
                new ExtractReference("razor:Shared.Field", CodeEdgeKinds.Instantiates, "Input", 1),
                new ExtractReference("razor:Shared.Field", CodeEdgeKinds.Uses, "Name", 1) { Qualifier = members },
                new ExtractReference("razor:Shared.Field", CodeEdgeKinds.Calls, "Saved", 1) { Qualifier = members },
                new ExtractReference("razor:Shared.Field", CodeEdgeKinds.Uses, "Value", 1) { Qualifier = members },
                new ExtractReference("razor:Shared.Field", CodeEdgeKinds.Calls, "Changed", 1) { Qualifier = members },
            ],
            extract.References);
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

    /// <summary>
    /// What the C# reader marks dynamic in a code block, and the scopes it declares, are the file's as they are a C# file's
    /// of its own: nothing the reader saw is lost on the way into the component's extract.
    /// </summary>
    [Fact]
    public void ACodeBlocksDynamicCodeAndScopes_AreTheFilesAsACSharpFilesAre()
    {
        var extract = Read("Shared/Go.razor", "@code {\n    void Start() { dynamic value = Get(); value.Run(); }\n}\n");
        var alone = new CSharpExtractor().Extract(new CodeFileInput("Shared/Go.cs", "namespace Shared; partial class Go {\n    void Start() { dynamic value = Get(); value.Run(); }\n}\n"));

        Assert.Equal(2, Assert.Single(extract.Dynamic).Line);
        Assert.Equal(alone.Dynamic, extract.Dynamic);
        Assert.Equal(alone.Scopes, extract.Scopes);
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
