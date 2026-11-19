using DssHarness.Core.Codebase;
using DssHarness.Core.Codebase.Languages;

namespace DssHarness.Tests;

/// <summary>MSBuild's projects and solutions, held to the contract's kit, and the rules of their own the kit does not name.</summary>
public sealed class MsbuildExtractorTests : CodeGraphExtractorContractTests<MsbuildExtractor>
{
    private const string Shapes = """
        <Project Sdk="Microsoft.NET.Sdk">

          <PropertyGroup>
            <Description>Shapes on a plane. Drawn and measured.</Description>
          </PropertyGroup>

          <ItemGroup>
            <ProjectReference Include="..\Geometry\Geometry.csproj" />
            <ProjectReference Include="../../tests/Shapes.Tests/Shapes.Tests.csproj;$(Root)\src\App\App.csproj"
                              Condition="'$(Configuration)' == 'Debug'" />
            <ProjectReference Include="..\**\*.csproj" />
          </ItemGroup>

        </Project>
        """;

    private const string Geometry = """
        <Project Sdk="Microsoft.NET.Sdk" />
        """;

    private const string Tests = """
        <?xml version="1.0" encoding="utf-8"?>
        <!-- The tests. -->
        <Project Sdk="Microsoft.NET.Sdk">
          <ItemGroup>
            <ProjectReference Include="../../src/Shapes/Shapes.csproj" />
          </ItemGroup>
        </Project>
        """;

    private const string Solution = """
        <Solution>
          <Folder Name="/src/">
            <Project Path="src/Shapes/Shapes.csproj" />
            <Project Path="src\Geometry\Geometry.csproj" />
          </Folder>
          <Project Path="tests/Shapes.Tests/Shapes.Tests.csproj" />
        </Solution>
        """;

    /// <inheritdoc/>
    public override IReadOnlyList<CodeGraphFixture> Fixtures { get; } =
    [
        new("src/Shapes/Shapes.csproj", Shapes)
        {
            Nodes = [new("msbuild:src/Shapes/Shapes.csproj", CodeNodeKinds.Project, (1, 14))],
            Edges =
            [
                new("msbuild:src/Shapes/Shapes.csproj", "msbuild:src/Geometry/Geometry.csproj", CodeEdgeKinds.ProjectReference, 8, CodeConfidences.Unique),
                new("msbuild:src/Shapes/Shapes.csproj", "msbuild:tests/Shapes.Tests/Shapes.Tests.csproj", CodeEdgeKinds.ProjectReference, 9, CodeConfidences.Unique),
            ],
        },
        new("src/Geometry/Geometry.csproj", Geometry)
        {
            Nodes = [new("msbuild:src/Geometry/Geometry.csproj", CodeNodeKinds.Project, (1, 1))],
        },
        new("tests/Shapes.Tests/Shapes.Tests.csproj", Tests)
        {
            Edges = [new("msbuild:tests/Shapes.Tests/Shapes.Tests.csproj", "msbuild:src/Shapes/Shapes.csproj", CodeEdgeKinds.ProjectReference, 5, CodeConfidences.Unique)],
        },
        new("Shapes.slnx", Solution)
        {
            Nodes = [new("msbuild:Shapes.slnx", CodeNodeKinds.Project, (1, 7))],
            Edges =
            [
                new("msbuild:Shapes.slnx", "msbuild:src/Shapes/Shapes.csproj", CodeEdgeKinds.ProjectReference, 3, CodeConfidences.Unique),
                new("msbuild:Shapes.slnx", "msbuild:src/Geometry/Geometry.csproj", CodeEdgeKinds.ProjectReference, 4, CodeConfidences.Unique),
                new("msbuild:Shapes.slnx", "msbuild:tests/Shapes.Tests/Shapes.Tests.csproj", CodeEdgeKinds.ProjectReference, 6, CodeConfidences.Unique),
            ],
        },
    ];

    /// <summary>
    /// A project is named by its file name, so a solution and the project it is named after are two; its header is its
    /// root element's line, and its summary the first sentence of its <c>Description</c>, whose key's summary is its whole
    /// value. A reference by a wildcard names
    /// no project, each project of a list is named, and each <c>$(Name)</c> the file writes - into an attribute as into
    /// an element - is a use of the key of that name.
    /// </summary>
    [Fact]
    public void AProject_IsNamedByItsFileName_WithItsHeader_ItsDescription_AndEachProjectItNames()
    {
        var shapes = Extractor.Extract(new CodeFileInput("src/Shapes/Shapes.csproj", Shapes));
        var tests = Extractor.Extract(new CodeFileInput("tests/Shapes.Tests/Shapes.Tests.csproj", Tests));
        var solution = Extractor.Extract(new CodeFileInput("Shapes.slnx", Solution));

        var project = shapes.Nodes[0];
        Assert.Equal(("Shapes.csproj", "src/Shapes/Shapes.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">", "Shapes on a plane."), (project.Name, project.Qualified, project.Signature, project.Summary));
        Assert.Equal([("msbuild:src/Shapes/Shapes.csproj#Description", "Shapes on a plane. Drawn and measured.")], shapes.Nodes.Skip(1).Select(node => (node.Id.Whole(), node.Summary)));
        Assert.Equal(
            [
                ("Geometry.csproj", CodeEdgeKinds.ProjectReference, 8),
                ("Shapes.Tests.csproj", CodeEdgeKinds.ProjectReference, 9),
                ("App.csproj", CodeEdgeKinds.ProjectReference, 9),
                ("Root", CodeEdgeKinds.Uses, 9),
                ("Configuration", CodeEdgeKinds.Uses, 10),
            ],
            shapes.References.Select(reference => (reference.Name, reference.Kind, reference.Line)));

        // The declaration and the comment before the root are no header.
        Assert.Equal("<Project Sdk=\"Microsoft.NET.Sdk\">", Assert.Single(tests.Nodes).Signature);
        Assert.Equal(("Shapes.slnx", null), (Assert.Single(solution.Nodes).Name, solution.Nodes[0].Summary));

        // A solution's Description is no summary of it: a solution has none, and an element so named is something else's.
        Assert.Null(Assert.Single(Extractor.Extract(new CodeFileInput("All.slnx", "<Solution><Description>Not a summary.</Description></Solution>")).Nodes).Summary);
        Assert.All(solution.References, reference => Assert.Equal(CodeEdgeKinds.ProjectReference, reference.Kind));
    }

    /// <summary>A document type's entities are never resolved: one it declares stops the parse where it is used, and what came before is kept.</summary>
    [Fact]
    public void ADocumentTypesEntities_AreNeverResolved()
    {
        const string Text = """
            <?xml version="1.0"?>
            <!DOCTYPE Project [ <!ENTITY outside SYSTEM "file:///etc/hostname"> <!ENTITY inside "In.csproj"> ]>
            <Project>
              <ItemGroup>
                <ProjectReference Include="a/One.csproj" />
                <ProjectReference Include="&inside;" />
                <ProjectReference Include="&outside;" />
              </ItemGroup>
            </Project>
            """;

        var extract = Extractor.Extract(new CodeFileInput("Entity.csproj", Text));

        Assert.Equal(["One.csproj"], extract.References.Select(reference => reference.Name));
        Assert.Equal([6], extract.Dynamic.Select(mark => mark.Line));
    }

    /// <summary>
    /// A file that stops parsing is read up to its fault, and marks it: the references before it are kept, the project is
    /// a node, and the line it stops at is dynamic, saying so - so a reference missing after it is never taken for one the
    /// project does not make. One that reads whole marks nothing.
    /// </summary>
    [Fact]
    public void AFileThatStopsParsing_IsReadUpToItsFault_AndMarksIt()
    {
        const string Text = """
            <Project>
              <ItemGroup>
                <ProjectReference Include="a/One.csproj" />
              </Wrong>
                <ProjectReference Include="b/Two.csproj" />
            </Project>
            """;

        var extract = Extractor.Extract(new CodeFileInput("Broken.csproj", Text));

        Assert.Equal(["One.csproj"], extract.References.Select(reference => reference.Name));
        Assert.Equal(("Broken.csproj", 1, 6), (Assert.Single(extract.Nodes).Name, extract.Nodes[0].Start, extract.Nodes[0].End));
        Assert.Equal([new ExtractDynamic(4, "the file stops reading as XML here (The 'ItemGroup' start tag on line 2 position 4 does not match the end tag of 'Wrong'), and nothing after it is read")], extract.Dynamic);
        Assert.Empty(Extractor.Extract(new CodeFileInput("src/Shapes/Shapes.csproj", Shapes)).Dynamic);
    }

    /// <summary>
    /// Each <c>$(Name)</c> is read where the file writes it - an attribute's value over several lines, an element's text
    /// beside an entity, a CDATA section - on the line it stands on, each a use of the key of that name an XML file
    /// declares; one a comment writes is none, and none is read past the place the file stops reading as XML.
    /// </summary>
    [Fact]
    public void EachPropertyIsAUseOfAnXmlKey_ReadWhereTheFileWritesIt_AndNeverInAComment()
    {
        const string Text = """
            <Project>
              <PropertyGroup Label="x"
                Condition="'$(One)' ==
                '$(Two)'">
                <Out>&amp;$(Three) <!-- $(Old) --> $(Four)</Out>
                <Note><![CDATA[
                  $(Five) ]]></Note>
              </PropertyGroup>
            </Project>
            <Late Value="$(Late)" />
            """;

        var extract = Extractor.Extract(new CodeFileInput("Uses.csproj", Text));

        Assert.Equal(
            [("One", 3), ("Two", 4), ("Three", 5), ("Four", 5), ("Five", 7)],
            extract.References.Select(reference => (reference.Name, reference.Line)));
        Assert.All(extract.References, reference => Assert.Equal((CodeEdgeKinds.Uses, new ExtractQualifier(CodeQualifierKinds.Key, "xml")), (reference.Kind, reference.Qualifier)));
        Assert.Equal([10], extract.Dynamic.Select(mark => mark.Line));
    }

    /// <summary>
    /// A property the project's own <c>PropertyGroup</c> declares is a key of the project's alone, its value its summary;
    /// a <c>$(Name)</c> read as properties are - in a <c>PropertyGroup</c> - reads those declared before it, its own
    /// declaration not among them, and one read once every property is - an item's - reads every one; a name the project
    /// declares none of before it reads the key an XML file declares. No other file reaches the project's own: another
    /// project's <c>$(Name)</c> and a C# literal of its name join none of them.
    /// </summary>
    [Fact]
    public void AProjectsOwnProperties_AreItsKeys_WhichItsUsesRead_AndNoOtherFileReaches()
    {
        const string Text = """
            <Project>
              <PropertyGroup>
                <Version>1.0</Version>
                <DefineConstants>$(DefineConstants);TRACE</DefineConstants>
                <Full>$(Version)-full</Full>
              </PropertyGroup>
              <PropertyGroup Condition="'$(Configuration)' == 'Release'">
                <Version>2.0</Version>
              </PropertyGroup>
              <ItemGroup>
                <None Include="$(Version).txt" />
              </ItemGroup>
            </Project>
            """;
        const string Props = """
            <Project>
              <PropertyGroup>
                <DefineConstants>DEBUG</DefineConstants>
              </PropertyGroup>
            </Project>
            """;

        var extract = Extractor.Extract(new CodeFileInput("App.csproj", Text));

        Assert.Equal(
            [
                ("msbuild:App.csproj#Version", "Version", 3, "1.0"),
                ("msbuild:App.csproj#DefineConstants", "DefineConstants", 4, "$(DefineConstants);TRACE"),
                ("msbuild:App.csproj#Full", "Full", 5, "$(Version)-full"),
                ("msbuild:App.csproj#Version~2", "Version", 8, "2.0"),
            ],
            extract.Nodes.Skip(1).Select(node => (node.Id, node.Name, node.Start, node.Summary)));
        Assert.All(extract.Nodes.Skip(1), node => Assert.Equal((CodeSubKinds.Key, "PropertyGroup", "msbuild:App.csproj"), (node.SubKind, node.Scope, node.Parent)));
        Assert.Equal([new ExtractScope("PropertyGroup", false)], extract.Scopes);
        Assert.Equal(
            [
                ("DefineConstants", 4, new ExtractQualifier(CodeQualifierKinds.Key, "xml")),
                ("Version", 5, new ExtractQualifier(CodeQualifierKinds.Own, "msbuild:App.csproj#Version")),
                ("Configuration", 7, new ExtractQualifier(CodeQualifierKinds.Key, "xml")),
                ("Version", 11, new ExtractQualifier(CodeQualifierKinds.Key, "msbuild")),
            ],
            extract.References.Select(reference => (reference.Name, reference.Line, reference.Qualifier)));

        var other = Extractor.Extract(new CodeFileInput("Other.csproj", "<Project>\n  <ItemGroup>\n    <None Include=\"$(Version)\" />\n  </ItemGroup>\n</Project>\n"));
        var code = new CSharpExtractor().Extract(new CodeFileInput("src/Read.cs", "namespace App;\n\npublic static class Read\n{\n    public static string Name() => \"Version\";\n}\n"));
        var linked = CodeGraphLinker.Link(
        [
            new CodeGraphFile("App.csproj", "msbuild", extract),
            new CodeGraphFile("Other.csproj", "msbuild", other),
            new CodeGraphFile("Directory.Build.props", "xml", new XmlExtractor().Extract(new CodeFileInput("Directory.Build.props", Props))),
            new CodeGraphFile("src/Read.cs", "csharp", code),
        ]);

        Assert.Equal(
            [
                ("msbuild:App.csproj", "xml:Directory.Build.props#DefineConstants", 4, CodeConfidences.Exact),
                ("msbuild:App.csproj", "msbuild:App.csproj#Version", 5, CodeConfidences.Exact),
                ("msbuild:App.csproj", "msbuild:App.csproj#Version", 11, CodeConfidences.Candidates),
                ("msbuild:App.csproj", "msbuild:App.csproj#Version~2", 11, CodeConfidences.Candidates),
            ],
            linked.Edges.Where(edge => edge.Kind == CodeEdgeKinds.Uses).Select(edge => (edge.From, edge.To, edge.Line, edge.Confidence)).OrderBy(edge => edge.Line).ThenBy(edge => edge.To, StringComparer.Ordinal));
    }
}
