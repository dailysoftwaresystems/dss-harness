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
    /// root element's line, and its summary the first sentence of its <c>Description</c>. A reference by a wildcard names
    /// no project, and each project of a list is named.
    /// </summary>
    [Fact]
    public void AProject_IsNamedByItsFileName_WithItsHeader_ItsDescription_AndEachProjectItNames()
    {
        var shapes = Extractor.Extract(new CodeFileInput("src/Shapes/Shapes.csproj", Shapes));
        var tests = Extractor.Extract(new CodeFileInput("tests/Shapes.Tests/Shapes.Tests.csproj", Tests));
        var solution = Extractor.Extract(new CodeFileInput("Shapes.slnx", Solution));

        var project = Assert.Single(shapes.Nodes);
        Assert.Equal(("Shapes.csproj", "src/Shapes/Shapes.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">", "Shapes on a plane."), (project.Name, project.Qualified, project.Signature, project.Summary));
        Assert.Equal([("Geometry.csproj", 8), ("Shapes.Tests.csproj", 9), ("App.csproj", 9)], shapes.References.Select(reference => (reference.Name, reference.Line)));

        // The declaration and the comment before the root are no header.
        Assert.Equal("<Project Sdk=\"Microsoft.NET.Sdk\">", Assert.Single(tests.Nodes).Signature);
        Assert.Equal(("Shapes.slnx", null), (Assert.Single(solution.Nodes).Name, solution.Nodes[0].Summary));

        // A solution's Description is no summary of it: a solution has none, and an element so named is something else's.
        Assert.Null(Assert.Single(Extractor.Extract(new CodeFileInput("All.slnx", "<Solution><Description>Not a summary.</Description></Solution>")).Nodes).Summary);
        Assert.All(shapes.References.Concat(solution.References), reference => Assert.Equal(CodeEdgeKinds.ProjectReference, reference.Kind));
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
        Assert.Equal([new ExtractDynamic(4, "the file stops reading as XML here, and nothing after it is read")], extract.Dynamic);
        Assert.Empty(Extractor.Extract(new CodeFileInput("src/Shapes/Shapes.csproj", Shapes)).Dynamic);
    }
}
