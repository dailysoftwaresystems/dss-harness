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
    /// its <c>@code</c> block is, read by the C# reader; a PascalCase element an instantiates, an <c>@on*</c> attribute
    /// that names a method a call of the component's member, of whatever count of arguments the event gives, and another
    /// <c>@</c> attribute a use of what its value reads.
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
    /// What a Razor comment holds is no directive and no markup, and a directive attribute's value is read by the C#
    /// reader by its name and its modifier: <c>@bind-Value</c> what its expression uses - a member of what <c>Model</c>
    /// names, which the file declares no field of - an arrow function's call a call of the component's member, and a
    /// modifier that holds no expression nothing.
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
                new ExtractReference("razor:src.my_app.Pages.Index", CodeEdgeKinds.Uses, "Name", 5) { Qualifier = new ExtractQualifier(CodeQualifierKinds.Named, "Model") },
                new ExtractReference("razor:src.my_app.Pages.Index", CodeEdgeKinds.Calls, "Select", 5) { Arity = 1, Qualifier = members },
                new ExtractReference("razor:src.my_app.Pages.Index", CodeEdgeKinds.Uses, "item", 5) { Qualifier = members },
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

    /// <summary>
    /// A component and the partial of its name its code-behind file declares - <c>Cart.razor.cs</c> beside
    /// <c>Cart.razor</c> - are joined by partOf as Razor pairs them, though the namespace the tree's path gives the
    /// component is not the one the partial writes: the project's root is the tree's no more. A partial of another name
    /// in that file, and one of the name in another file, is no part of it.
    /// </summary>
    [Fact]
    public void AComponentAndItsCodeBehind_AreJoinedByTheirFiles_WhereverTheProjectStands()
    {
        var linked = CodeGraphLinker.Link(
        [
            new CodeGraphFile("src/Shop/Pages/Cart.razor", "razor", Read("src/Shop/Pages/Cart.razor", "<p>Cart</p>\n")),
            new CodeGraphFile("src/Shop/Pages/Cart.razor.cs", "csharp", new CSharpExtractor().Extract(new CodeFileInput("src/Shop/Pages/Cart.razor.cs", "namespace Shop.Pages;\npublic partial class Cart { }\npublic partial class Other { }\n"))),
            new CodeGraphFile("src/Shop/Pages/Elsewhere.cs", "csharp", new CSharpExtractor().Extract(new CodeFileInput("src/Shop/Pages/Elsewhere.cs", "namespace Other.Pages;\npublic partial class Cart { }\n"))),
        ]);

        Assert.Equal(
            [new CodeEdge("razor:src.Shop.Pages.Cart", "csharp:Shop.Pages.Cart", CodeEdgeKinds.PartOf, 1, CodeConfidences.Unique)],
            linked.Edges.Where(edge => edge.Kind == CodeEdgeKinds.PartOf));
    }

    /// <summary>
    /// A name the markup's own C# declares where an attribute stands is the markup's, no member of the component: an
    /// <c>@foreach</c>'s item inside its block and a member past it, a <c>@{ }</c> block's local from where it is declared
    /// and a member before it, a pattern's variable inside its <c>@if</c>, an arrow function's parameter in its own value -
    /// a member of it a member of that value - and a template's <c>context</c> - or the name its <c>Context=</c> gives,
    /// past which <c>context</c> is a member again - inside a component's content; what a statement's own head reads is
    /// read too.
    /// </summary>
    [Fact]
    public void ANameTheMarkupsCodeDeclares_IsItsOwn_AndNoMember()
    {
        var extract = Read("Shared/List.razor", """
            @foreach (var item in Items)
            {
                <Row Item="@item" Label="@label" />
            }
            <Row Item="@item" />
            @{ var label = "x"; }
            <Row Label="@label" @onclick="e => e.Stop()" />
            @if (Current is string product)
            {
                <Row Item="@product" />
            }
            <Grid Items="@Items">
                <Cell Value="@context" />
            </Grid>
            <Grid Items="@Items" Context="row">
                <Cell Value="@row" Other="@context" />
            </Grid>
            @code {
                private string item = "";
                private string label = "";
                private string product = "";
                private string context = "";
                private string row = "";
                private string[] Items = [];
                private object Current = "";
            }
            """);

        Assert.Equal(
            [
                ("Items", 1, CodeQualifierKinds.This), ("label", 3, CodeQualifierKinds.This), ("item", 5, CodeQualifierKinds.This), ("Stop", 7, CodeQualifierKinds.Value),
                ("Current", 8, CodeQualifierKinds.This), ("Items", 12, CodeQualifierKinds.This), ("Items", 15, CodeQualifierKinds.This), ("context", 16, CodeQualifierKinds.This),
            ],
            extract.References.Where(reference => reference.From == "razor:Shared.List" && reference.Kind is CodeEdgeKinds.Uses or CodeEdgeKinds.Calls).Select(reference => (reference.Name, reference.Line, reference.Qualifier?.Kind)));
    }

    /// <summary>
    /// An <c>@inject</c> declares a property of the component, of the type it is given - a reference of that type, no use
    /// of it - and a use of the property by its name, in the markup and in the <c>@code</c> block alike, binds to it.
    /// </summary>
    [Fact]
    public void AnInjectedService_IsAPropertyOfTheComponent_AndEachUseOfItBindsToIt()
    {
        var extract = Read("Shared/Checkout.razor", """
            @inject CartService Carts
            <Summary Total="@Carts.Total" />
            @code {
                private void Pay()
                {
                    Carts.Add(1);
                }
            }
            """);

        var carts = Assert.Single(extract.Nodes, node => node.Id == "razor:Shared.Checkout.Carts");

        Assert.Equal(("property", 1, 1), (carts.SubKind, carts.Start, carts.End));
        Assert.Equal([(CodeEdgeKinds.References, "CartService")], extract.References.Where(reference => reference.From == "razor:Shared.Checkout.Carts").Select(reference => (reference.Kind, reference.Name)));

        var linked = CodeGraphLinker.Link([new CodeGraphFile("Shared/Checkout.razor", "razor", extract)]);

        Assert.Equal(
            [
                new CodeEdge("razor:Shared.Checkout", "razor:Shared.Checkout.Carts", CodeEdgeKinds.Uses, 2, CodeConfidences.Exact),
                new CodeEdge("razor:Shared.Checkout.Pay()", "razor:Shared.Checkout.Carts", CodeEdgeKinds.Uses, 6, CodeConfidences.Exact),
            ],
            linked.Edges.Where(edge => edge.To == "razor:Shared.Checkout.Carts" && edge.Kind == CodeEdgeKinds.Uses).OrderBy(edge => edge.Line));
    }

    /// <summary>
    /// A <c>@{ }</c> block's local is held to the block of C# that holds it - its braces open no scope of their own - so a
    /// block two statements deep holds it to the inner statement's close: the markup past it, in the outer statement and
    /// after it, reads the component's member of that name.
    /// </summary>
    [Fact]
    public void ALocalOfABlockTwoStatementsDeep_IsHeldToTheInnerStatement()
    {
        var extract = Read("Shared/Grid.razor", """
            @foreach (var row in Rows)
            {
                @if (row.Open)
                {
                    @{ var label = row.Name; }
                    <Cell Text="@label" />
                }
                <Cell Text="@label" />
            }
            <Cell Text="@label" />
            @code {
                private string label = "x";
                private List<Row> Rows = new();
            }
            """);

        Assert.Equal(
            [("Rows", 1), ("label", 8), ("label", 10)],
            extract.References.Where(reference => reference.From == "razor:Shared.Grid" && reference.Kind == CodeEdgeKinds.Uses && reference.Qualifier?.Kind == CodeQualifierKinds.This).Select(reference => (reference.Name, reference.Line)));
        Assert.Equal(
            [("razor:Shared.Grid.Rows", 1), ("razor:Shared.Grid.label", 8), ("razor:Shared.Grid.label", 10)],
            CodeGraphLinker.Link([new CodeGraphFile("Shared/Grid.razor", "razor", extract)]).Edges.Where(edge => edge.From == "razor:Shared.Grid" && edge.Kind == CodeEdgeKinds.Uses).Select(edge => (edge.To, edge.Line)));
    }

    /// <summary>
    /// The C# of a <c>@{ }</c> block and of a statement's body is no markup: a type argument, a cast and a comparison
    /// written without spaces open no element, so none is an instantiates and none holds the markup after it; the elements
    /// a statement's body switches to are markup, read where they stand, and what follows reads the member it names.
    /// </summary>
    [Fact]
    public void TheCSharpOfABlockAndOfAStatementsBody_IsNoMarkup()
    {
        var extract = Read("Shared/Taxes.razor", """
            @{
                var items = (IEnumerable<CartItem>)Lines;
            }
            @foreach (var item in items)
            {
                var taxes = new List<Tax>();
                for (var at = 0; at<Count; at++) { }
                <Cell Value="@item" />
            }
            <Cell Value="@context" />
            @code { private string context = ""; private int Count; private object Lines = 1; }
            """);

        Assert.Equal(
            [
                ("Cell", CodeEdgeKinds.Instantiates, 8), ("Cell", CodeEdgeKinds.Instantiates, 10), ("IEnumerable`1", CodeEdgeKinds.References, 2), ("CartItem", CodeEdgeKinds.References, 2),
                ("Lines", CodeEdgeKinds.Uses, 2), ("List`1", CodeEdgeKinds.Instantiates, 6), ("Tax", CodeEdgeKinds.References, 6), ("Count", CodeEdgeKinds.Uses, 7), ("context", CodeEdgeKinds.Uses, 10),
            ],
            extract.References.Where(reference => reference.From == "razor:Shared.Taxes").Select(reference => (reference.Name, reference.Kind, reference.Line)));
    }

    /// <summary>
    /// A statement's continuations - an <c>else if</c>, a <c>catch</c> - are read with it, written with no <c>@</c> as
    /// Razor writes them, so what their heads declare is theirs: a pattern's variable, a catch's, and a local a body
    /// declares with no <c>@{ }</c> are no members, and what the heads read is read.
    /// </summary>
    [Fact]
    public void AStatementsContinuations_AndItsBodysLocals_DeclareWhatTheyName()
    {
        var extract = Read("Shared/Stmt.razor", """
            @foreach (var item in Items)
            {
                var total = item.Price * 2;
                <Cell Value="@total" />
            }
            @if (Current is Order order)
            {
                <Cell Value="@order" />
            }
            else if (Current is Refund refund)
            {
                <Cell Value="@refund" />
            }
            @try
            {
                <Cell Value="@Items" />
            }
            catch (Exception error) when (error.Data != null)
            {
                <Cell Value="@error" />
            }
            finally
            {
                <Cell Value="@total" />
            }
            @code {
                private string total = "";
                private string refund = "";
                private string error = "";
                private int[] Items = [];
                private object Current = 1;
            }
            """);

        Assert.Equal(
            [("Items", 1), ("Current", 6), ("Current", 10), ("Items", 16), ("total", 24)],
            extract.References.Where(reference => reference.From == "razor:Shared.Stmt" && reference.Qualifier?.Kind == CodeQualifierKinds.This).Select(reference => (reference.Name, reference.Line)));
        Assert.Equal(
            [("Order", 6), ("Refund", 10), ("Exception", 18)],
            extract.References.Where(reference => reference.Kind == CodeEdgeKinds.References).Select(reference => (reference.Name, reference.Line)));
    }

    /// <summary>
    /// An expression written <c>@( )</c> in an attribute's value is read as its bare form is: a directive attribute's an
    /// arrow function's call, another attribute's a use of what it reads.
    /// </summary>
    [Fact]
    public void AnExpressionInAnAttributesValue_IsReadAsItsBareFormIs()
    {
        var extract = Read("Shared/Expr.razor", """
            <button @onclick="() => Pay(1)">a</button>
            <button @onclick="@(() => Pay(2))">b</button>
            <Row Value="@(Total)" />
            <Row Value="@Total" />
            @code {
                private void Pay(int n) {}
                private int Total;
            }
            """);

        Assert.Equal(
            [(CodeEdgeKinds.Calls, "Pay", 1, (int?)1), (CodeEdgeKinds.Calls, "Pay", 2, 1), (CodeEdgeKinds.Uses, "Total", 3, null), (CodeEdgeKinds.Uses, "Total", 4, null)],
            extract.References.Where(reference => reference.Kind is CodeEdgeKinds.Calls or CodeEdgeKinds.Uses).Select(reference => (reference.Kind, reference.Name, reference.Line, reference.Arity)));
    }

    /// <summary>
    /// A view's expressions are its code - <c>@Model.Name</c> a use of the model's member, in an element's attribute, an
    /// <c>@:</c> line and a statement's markup alike - and what is text is none: an address's <c>@</c>, an <c>@@</c>,
    /// <c>&lt;text&gt;</c>'s words; a string its code writes is a literal of the view's.
    /// </summary>
    [Fact]
    public void AViewsExpressions_AreItsCode_AndItsTextIsNone()
    {
        var extract = Read("Views/Home/Index.cshtml", """
            @model ShopModel
            @{
                ViewData["Title"] = "Home";
            }
            <p>Mail us at sales@example.com or @@handle</p>
            @if (Model.Show)
            {
                <Widget Value="@Model.Name" />
                @:Plain text @Model.Total
                <text>More <b>words</b></text>
            }
            """);
        var model = new ExtractQualifier(CodeQualifierKinds.Type, "ShopModel");

        Assert.Equal(
            [
                new ExtractReference("razor:Views/Home/Index.cshtml#view", CodeEdgeKinds.Instantiates, "Widget", 8),
                new ExtractReference("razor:Views/Home/Index.cshtml#view", CodeEdgeKinds.Uses, "ViewData", 3),
                new ExtractReference("razor:Views/Home/Index.cshtml#view", CodeEdgeKinds.Uses, "Show", 6) { Qualifier = model },
                new ExtractReference("razor:Views/Home/Index.cshtml#view", CodeEdgeKinds.Uses, "Model", 6),
                new ExtractReference("razor:Views/Home/Index.cshtml#view", CodeEdgeKinds.Uses, "Name", 8) { Qualifier = model },
                new ExtractReference("razor:Views/Home/Index.cshtml#view", CodeEdgeKinds.Uses, "Model", 8),
                new ExtractReference("razor:Views/Home/Index.cshtml#view", CodeEdgeKinds.Uses, "Total", 9) { Qualifier = model },
                new ExtractReference("razor:Views/Home/Index.cshtml#view", CodeEdgeKinds.Uses, "Model", 9),
            ],
            extract.References.Where(reference => reference.From == "razor:Views/Home/Index.cshtml#view"));
        Assert.Equal([("Title", 3), ("Home", 3)], (extract.Literals ?? []).Select(literal => (literal.Value, literal.Line)));
    }

    /// <summary>
    /// A page's route is a string its class is given, a layout a type it names, an <c>@attribute</c> an attribute of its
    /// class, and a generic component's class is generic by its <c>@typeparam</c>s - named as the C# reader names a generic
    /// type, by its count of them, so its members are the class's own.
    /// </summary>
    [Fact]
    public void ARouteALayoutAnAttributeAndTypeParameters_AreTheClasss()
    {
        var extract = Read("Shared/Grid.razor", """
            @page "/items/{id:int}"
            @layout MainLayout
            @typeparam TItem where TItem : class
            @attribute [Authorize]
            <p>@Selected</p>
            @code {
                [Parameter] public TItem? Selected { get; set; }
            }
            """);
        var component = Assert.Single(extract.Nodes, node => node.SubKind == "component");

        Assert.Equal(("razor:Shared.Grid`1", "Grid", "Shared.Grid`1"), (component.Id, component.Name, component.Qualified));
        Assert.Equal("razor:Shared.Grid`1", Assert.Single(extract.Nodes, node => node.Name == "Selected").Parent);
        Assert.Equal(
            [(CodeEdgeKinds.References, "MainLayout", 2), (CodeEdgeKinds.References, "AuthorizeAttribute", 4), (CodeEdgeKinds.Uses, "Selected", 5)],
            extract.References.Where(reference => reference.From == component.Id).Select(reference => (reference.Kind, reference.Name, reference.Line)));
        Assert.Equal([("/items/{id:int}", 1, component.Id)], (extract.Literals ?? []).Select(literal => (literal.Value, literal.Line, literal.Node)));
    }

    /// <summary>
    /// The walk that reads where Razor's code and markup are holds what is open on a stack of its own, so markup nested
    /// deeper than any thread's stack - twenty thousand elements inside a statement's block, read on a thread with a small
    /// stack - is read whole: the expression inside them is the component's code, on its line.
    /// </summary>
    [Fact]
    public void MarkupNestedDeeperThanAStack_IsReadWhole()
    {
        const int Depth = 20_000;
        var text = $"@if (shown)\n{{\n{string.Concat(Enumerable.Repeat("<div>", Depth))}\n<b>@deep</b>\n{string.Concat(Enumerable.Repeat("</div>", Depth))}\n}}\n";
        FileExtract? extract = null;
        var reading = new Thread(() => extract = Read("Shared/Deep.razor", text), maxStackSize: 256 * 1024);

        reading.Start();
        Assert.True(reading.Join(TimeSpan.FromMinutes(2)), "the deep file was not read in two minutes");

        Assert.Equal(
            [("shown", 1), ("deep", 4)],
            extract!.References.Where(reference => reference.Kind == CodeEdgeKinds.Uses).Select(reference => (reference.Name, reference.Line)));
    }

    private static FileExtract Read(string path, string text) => new RazorExtractor().Extract(new CodeFileInput(path, text));
}
