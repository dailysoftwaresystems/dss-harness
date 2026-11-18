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
                new("xml:app.config#configuration/appSettings/add/Main", CodeNodeKinds.Member, (3, 3)),
                new("xml:app.config#configuration/appSettings/add/Other", CodeNodeKinds.Member, (4, 4)),
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
                new("xml:Views/Cart.xaml#UserControl/StackPanel/Root", CodeNodeKinds.Member, (4, 6)),
                new("xml:Views/Cart.xaml#UserControl/StackPanel/Button/Checkout", CodeNodeKinds.Member, (5, 5)),
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
        var first = Assert.Single(extract.Nodes, node => node.Id == "xml:app.config#configuration/appSettings/add/Main");

        Assert.Equal(("Main", "element"), (first.Name, first.SubKind));
        Assert.Equal("xml:app.config#configuration", first.Parent);
        Assert.DoesNotContain(extract.Nodes, node => node.Name == "appSettings");

        var plain = Read("list.xml", Plain);

        Assert.Equal(3, plain.Nodes.Count);
        Assert.DoesNotContain(plain.Nodes, node => node.Name == "skipped");
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

    /// <summary>A <c>.xaml</c> root's <c>x:Class</c> makes the root a type marked a part of that C# type, named by it; a file with a DTD is refused.</summary>
    [Fact]
    public void AXamlRootsXClassMakesItAPartOfThatCSharpType_AndAFileWithADtdIsRefused()
    {
        var extract = Read("Views/Cart.xaml", Xaml);
        var root = Assert.Single(extract.Nodes, node => node.Id == "xml:Shop.Cart");

        Assert.Equal(("Cart", "Shop.Cart", "view", true), (root.Name, root.Qualified, root.SubKind, root.Part));

        var refused = Read("doc.xml", "<!DOCTYPE root [<!ENTITY x \"y\">]>\n<root>&x;</root>");

        Assert.Empty(refused.Nodes);
        Assert.Single(refused.Dynamic);
    }

    private static FileExtract Read(string path, string text) => new XmlExtractor().Extract(new CodeFileInput(path, text));
}
