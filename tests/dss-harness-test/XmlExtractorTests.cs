using DssHarness.Core.Codebase;
using DssHarness.Core.Codebase.Languages;

namespace DssHarness.Tests;

/// <summary>An XML data file, held to the contract's kit, and the rules of its own the kit does not name.</summary>
public sealed class XmlExtractorTests : CodeGraphExtractorContractTests<XmlExtractor>
{
    private const string Props = """
        <Project>
          <PropertyGroup>
            <OutputPath>bin</OutputPath>
            <TargetFramework>net9.0</TargetFramework>
          </PropertyGroup>
          <ItemGroup>
            <Compile Include="src" />
          </ItemGroup>
        </Project>
        """;

    private const string Common = """
        <Project>
          <PropertyGroup>
            <OutputPath>bin</OutputPath>
          </PropertyGroup>
        </Project>
        """;

    private const string Config = """
        <configuration>
          <appSettings>
            <add key="Main" value="one" />
            <add key="Other" value="two" />
          </appSettings>
          <connectionStrings>
            <add name="Orders" connectionString="Server=db;Database=orders" />
          </connectionStrings>
          <mySection>
            <add key="Custom" value="three" />
          </mySection>
        </configuration>
        """;

    private const string Resx = """
        <root>
          <resheader name="resmimetype">
            <value>text/microsoft-resx</value>
          </resheader>
          <data name="Greeting" xml:space="preserve">
            <value>Hello</value>
          </data>
          <data name="Farewell">
            <value>Bye</value>
          </data>
        </root>
        """;

    private const string Xaml = """
        <UserControl x:Class="Shop.Cart"
            xmlns="http://schemas.microsoft.com/winfx/2009/xaml"
            xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml">
          <StackPanel x:Name="Root">
            <Button x:Name="Checkout" />
          </StackPanel>
        </UserControl>
        """;

    private const string Plain = """<list><item id="one" /><item id="two" note="skipped" /></list>""";

    private const string Manifest = """<assembly manifestVersion="1.0" />""";

    private const string Nuspec = """<package><metadata id="billing" version="1.0" /></package>""";

    /// <inheritdoc/>
    public override IReadOnlyList<CodeGraphFixture> Fixtures { get; } =
    [
        new("Directory.Build.props", Props)
        {
            Nodes =
            [
                new("xml:Directory.Build.props#Project", CodeNodeKinds.Section, (1, 9)),
                new("xml:Directory.Build.props#OutputPath", CodeNodeKinds.Member, (3, 3)),
                new("xml:Directory.Build.props#TargetFramework", CodeNodeKinds.Member, (4, 4)),
            ],
        },
        new("common.targets", Common)
        {
            Nodes = [new("xml:common.targets#OutputPath", CodeNodeKinds.Member, (3, 3))],
        },
        new("app.config", Config)
        {
            Nodes =
            [
                new("xml:app.config#appSettings.Main", CodeNodeKinds.Member, (3, 3)),
                new("xml:app.config#appSettings.Other", CodeNodeKinds.Member, (4, 4)),
                new("xml:app.config#connectionStrings.Orders", CodeNodeKinds.Member, (7, 7)),
                new("xml:app.config#configuration/mySection/add/Custom", CodeNodeKinds.Member, (10, 10)),
            ],
        },
        new("Strings.resx", Resx)
        {
            Nodes =
            [
                new("xml:Strings.resx#root/resheader/resmimetype", CodeNodeKinds.Member, (2, 4)),
                new("xml:Strings.resx#Greeting", CodeNodeKinds.Member, (5, 7)),
                new("xml:Strings.resx#Farewell", CodeNodeKinds.Member, (8, 10)),
            ],
        },
        new("Views/Cart.xaml", Xaml)
        {
            Nodes =
            [
                new("xml:Shop.Cart", CodeNodeKinds.Type, (1, 7)),
                new("xml:Shop.Cart.Root", CodeNodeKinds.Member, (4, 6)),
                new("xml:Shop.Cart.Checkout", CodeNodeKinds.Member, (5, 5)),
            ],
        },
        new("list.xml", Plain)
        {
            Nodes =
            [
                new("xml:list.xml#list", CodeNodeKinds.Section, (1, 1)),
                new("xml:list.xml#list/item/one", CodeNodeKinds.Member, (1, 1)),
                new("xml:list.xml#list/item/two", CodeNodeKinds.Member, (1, 1)),
            ],
        },
        new("app.manifest", Manifest),
        new("billing.nuspec", Nuspec),
    ];

    /// <summary>
    /// A <c>.props</c> or <c>.targets</c> file's <c>PropertyGroup</c> children are keys named by the property they are,
    /// its <c>Import Project</c> an import of the file it names, and MSBuild's projects keep their own extractor.
    /// </summary>
    [Fact]
    public void APropsPropertyGroupsChildrenAreKeys_AndItsImportNamesAFile()
    {
        var extract = Read("Directory.Build.props", Props);
        var property = Assert.Single(extract.Nodes, node => node.Id == "xml:Directory.Build.props#OutputPath");

        Assert.Equal(("OutputPath", "key", "bin"), (property.Name, property.SubKind, property.Summary));
        Assert.Equal("msbuild", CodeGraphExtractors.OfThisBuild.Route("app.csproj", string.Empty).Extractor?.Language);

        var importing = Read("imports.props", "<Project>\n  <Import Project=\"common.props\" />\n</Project>\n");

        Assert.Equal([("common.props", 2)], importing.Imports.Select(import => (import.Target, import.Line)));
        Assert.All(importing.Imports, import => Assert.Equal(CodeImportForms.Relative, import.Form));
    }

    /// <summary>An element with an id-like attribute - name, key, id, x:Name - is an element node named by it; one with none is no node of its own.</summary>
    [Fact]
    public void AnElementWithAnIdLikeAttributeIsAnElementNode_AndOneWithNoneIsNoNodeOfItsOwn()
    {
        var extract = Read("app.config", Config);
        var custom = Assert.Single(extract.Nodes, node => node.Id == "xml:app.config#configuration/mySection/add/Custom");

        Assert.Equal(("Custom", "element"), (custom.Name, custom.SubKind));
        Assert.Equal("xml:app.config#configuration", custom.Parent!.Whole());
        Assert.DoesNotContain(extract.Nodes, node => node.Name == "appSettings");

        var plain = Read("list.xml", Plain);

        Assert.Equal(3, plain.Nodes.Count);
        Assert.DoesNotContain(plain.Nodes, node => node.Name == "skipped");
    }

    /// <summary>
    /// A <c>.config</c>'s <c>appSettings</c> <c>&lt;add key&gt;</c> and <c>connectionStrings</c> <c>&lt;add name&gt;</c>
    /// are keys named by the key they are, inside the section they are read from, their value or connection string as their
    /// summary - so .NET's read of one by its key, <c>AppSettings["Main"]</c>, is joined to it as any key's literal is.
    /// </summary>
    [Fact]
    public void AConfigsAppSettingsAndConnectionStrings_AreKeys_WhichItsReadsAreJoinedTo()
    {
        var extract = Read("app.config", Config);

        Assert.Equal(
            [
                ("xml:app.config#appSettings.Main", "Main", "appSettings.Main", "one", "xml:app.config#configuration"),
                ("xml:app.config#appSettings.Other", "Other", "appSettings.Other", "two", "xml:app.config#configuration"),
                ("xml:app.config#connectionStrings.Orders", "Orders", "connectionStrings.Orders", "Server=db;Database=orders", "xml:app.config#configuration"),
            ],
            extract.Nodes.Where(node => node.SubKind == CodeSubKinds.Key).Select(node => (node.Id, node.Name, node.Qualified, node.Summary, node.Parent)));

        const string Reader = """
            using System.Configuration;

            namespace Shop;

            public static class Settings
            {
                public static string Main() => ConfigurationManager.AppSettings["Main"];

                public static string Orders() => ConfigurationManager.ConnectionStrings["Orders"].ConnectionString;
            }
            """;
        var code = new CSharpExtractor().Extract(new CodeFileInput("src/Settings.cs", Reader));
        var linked = CodeGraphLinker.Link([new CodeGraphFile("app.config", "xml", extract), new CodeGraphFile("src/Settings.cs", "csharp", code)]);

        Assert.Equal(
            [("xml:app.config#appSettings.Main", 7), ("xml:app.config#connectionStrings.Orders", 9)],
            linked.Edges.Where(edge => edge is { Kind: CodeEdgeKinds.Uses, Confidence: CodeConfidences.Candidates } && edge.To.StartsWith("xml:", StringComparison.Ordinal)).Select(edge => (edge.To, edge.Line)).Order());
    }

    /// <summary>A <c>.resx</c>'s <c>data name=</c> are keys named by the name they carry; its <c>resheader</c> is an element like any other.</summary>
    [Fact]
    public void AResxsDataNamesAreKeys()
    {
        var extract = Read("Strings.resx", Resx);
        var greeting = Assert.Single(extract.Nodes, node => node.Id == "xml:Strings.resx#Greeting");

        Assert.Equal(("Greeting", "key", "Hello"), (greeting.Name, greeting.SubKind, greeting.Summary));
        Assert.Equal("resmimetype", Assert.Single(extract.Nodes, node => node.SubKind == "element").Name);
    }

    /// <summary>
    /// A <c>.xaml</c> root's <c>x:Class</c> makes the root a type marked a part of that C# type, named by it, and each
    /// element it names by <c>x:Name</c> a field of that type, of the element's type - which the code-behind reaches by the
    /// field's name, as it reaches a field of its own; a file with a DTD is refused.
    /// </summary>
    [Fact]
    public void AXamlRootsXClassMakesItAPartOfThatCSharpType_ItsNamedElementsItsFields_AndAFileWithADtdIsRefused()
    {
        var extract = Read("Views/Cart.xaml", Xaml);
        var root = Assert.Single(extract.Nodes, node => node.Id == "xml:Shop.Cart");

        Assert.Equal(("Cart", "Shop.Cart", "view", true), (root.Name, root.Qualified, root.SubKind, root.Part));
        Assert.Equal(
            [
                ("xml:Shop.Cart.Root", "Root", "Shop.Cart.Root", "field", "Shop.Cart", "xml:Shop.Cart", "StackPanel"),
                ("xml:Shop.Cart.Checkout", "Checkout", "Shop.Cart.Checkout", "field", "Shop.Cart", "xml:Shop.Cart", "Button"),
            ],
            extract.Nodes.Where(node => node.Kind == CodeNodeKinds.Member).Select(node => (node.Id.Whole(), node.Name, node.Qualified, node.SubKind, node.Scope, node.Parent, node.ValueType)));

        const string CodeBehind = """
            namespace Shop;

            public partial class Cart
            {
                public void Wire()
                {
                    Show(Checkout);
                    this.Root.Focus();
                }

                private static void Show(object shown)
                {
                }
            }
            """;
        var code = new CSharpExtractor().Extract(new CodeFileInput("Views/Cart.xaml.cs", CodeBehind));
        var linked = CodeGraphLinker.Link([new CodeGraphFile("Views/Cart.xaml", "xml", extract), new CodeGraphFile("Views/Cart.xaml.cs", "csharp", code)]);
        var wire = Assert.Single(code.Nodes, node => node.Name == "Wire");

        Assert.Equal(
            [new CodeEdge(wire.Id.Whole(), "xml:Shop.Cart.Checkout", CodeEdgeKinds.Uses, 7, CodeConfidences.Exact), new CodeEdge(wire.Id.Whole(), "xml:Shop.Cart.Root", CodeEdgeKinds.Uses, 8, CodeConfidences.Exact)],
            linked.Edges.Where(edge => edge.From == wire.Id && edge.To.StartsWith("xml:", StringComparison.Ordinal)));

        var refused = Read("doc.xml", "<!DOCTYPE root [<!ENTITY x \"y\">]>\n<root>&x;</root>");

        Assert.Empty(refused.Nodes);
        Assert.Equal([new ExtractDynamic(1, "the file declares a DTD, which is never read, and nothing after it is read")], refused.Dynamic);

        // A root whose x:Class names no type - one ending in a dot - is a section, as every other root is.
        Assert.Equal(
            [("xml:Views/Odd.xaml#Window", CodeNodeKinds.Section)],
            Read("Views/Odd.xaml", "<Window x:Class=\"Shop.\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" />").Nodes.Select(node => (node.Id.Whole(), node.Kind)));
    }

    /// <summary>
    /// A file that nests a hundred thousand elements deep is read whole, in a moment and on a thread whose stack is a
    /// fraction of what reading it by recursion would take: an element named at the bottom is a node named by its whole
    /// path, and what follows the nesting is read - while an element that names nothing is given no path, so the levels
    /// that name nothing cost no more than their reading.
    /// </summary>
    [Fact]
    public void AFileNestedHoweverDeep_IsReadWhole()
    {
        const int Depth = 100_000;
        var text = "<root>\n" + string.Concat(Enumerable.Repeat("<a>", Depth)) + "<item name=\"inside\" />" + string.Concat(Enumerable.Repeat("</a>", Depth)) + "\n<item name=\"after\" />\n</root>\n";
        var watch = System.Diagnostics.Stopwatch.StartNew();
        FileExtract? extract = null;
        var thread = new Thread(() => extract = Read("deep.xml", text), 256 * 1024);

        thread.Start();
        thread.Join();

        Assert.Equal(
            ["xml:deep.xml#root", $"xml:deep.xml#root/{string.Join('/', Enumerable.Repeat("a", Depth))}/item/inside", "xml:deep.xml#root/item/after"],
            extract!.Nodes.Select(node => node.Id.Whole()));
        Assert.Empty(extract.Dynamic);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"the file took {watch.Elapsed} to read");
    }

    /// <summary>
    /// Every node an XML file gives is one the core holds: an element's long path is whole in its id and in its signature
    /// alike; an id-like attribute of white space names nothing; every element is a
    /// node, however many; and where the file stops reading as XML, the reader's reason is said.
    /// </summary>
    [Fact]
    public void EveryNodeAnXmlFileGives_IsOneTheCoreHolds()
    {
        var segment = new string('s', 60);
        var deep = $"<root>{string.Concat(Enumerable.Repeat($"<{segment}>", 5))}<item name=\"x\" /><item name=\"  \" />{string.Concat(Enumerable.Repeat($"</{segment}>", 5))}</root>";
        var extract = Read("long.xml", deep);
        var item = Assert.Single(extract.Nodes, node => node.SubKind == CodeSubKinds.Element);

        Assert.Equal($"root/{string.Join('/', Enumerable.Repeat(segment, 5))}/item/x", item.Qualified);
        Assert.Equal(item.Qualified, item.Signature);
        Assert.Null(extract.WithDigests(deep).Problem("xml", 1));

        var many = $"<root>\n{string.Concat(Enumerable.Range(0, 12_000).Select(at => $"<item name=\"i{at}\" />\n"))}</root>\n";
        var counted = Read("many.xml", many);

        Assert.Equal(["xml:many.xml#root", .. Enumerable.Range(0, 12_000).Select(at => $"xml:many.xml#root/item/i{at}")], counted.Nodes.Select(node => node.Id.Whole()));

        var broken = Read("broken.xml", "<root>\n  <item name=\"a\">\n</root>\n");

        Assert.Equal(["xml:broken.xml#root", "xml:broken.xml#root/item/a"], broken.Nodes.Select(node => node.Id.Whole()));
        Assert.Equal([new ExtractDynamic(3, "the file stops reading as XML here (The 'item' start tag on line 2 position 4 does not match the end tag of 'root'), and nothing after it is read")], broken.Dynamic);
    }

    private static FileExtract Read(string path, string text) => new XmlExtractor().Extract(new CodeFileInput(path, text));
}
