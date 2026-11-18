using DssHarness.Core.Codebase;
using DssHarness.Core.Codebase.Languages;

namespace DssHarness.Tests;

/// <summary>A YAML data file, held to the contract's kit, and the rules of its own the kit does not name.</summary>
public sealed class YamlExtractorTests : CodeGraphExtractorContractTests<YamlExtractor>
{
    private const string Values = """
        base: &base
          level: info
          retries: 3
        service:
          <<: *base
          name: billing
        targets:
          x64:
            callingConvention: cdecl
        steps:
          - run: dotnet build
            name: build
          - run: dotnet test
        """;

    private const string Workflow = """
        name: ci
        on:
          push:
            branches: [ main ]
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - uses: actions/checkout@v4
              - name: deploy
                uses: ./.github/actions/deploy
                with:
                  target: production
        """;

    private const string Action = """
        name: deploy
        runs:
          using: node20
        """;

    /// <inheritdoc/>
    public override IReadOnlyList<CodeGraphFixture> Fixtures { get; } =
    [
        new("values.yml", Values)
        {
            Nodes =
            [
                new("yaml:values.yml#base.level", CodeNodeKinds.Member, (2, 2)),
                new("yaml:values.yml#service.name", CodeNodeKinds.Member, (6, 6)),
                new("yaml:values.yml#targets.x64.callingConvention", CodeNodeKinds.Member, (9, 9)),
                new("yaml:values.yml#steps[0].name", CodeNodeKinds.Member, (12, 12)),
            ],
        },
        new(".github/workflows/ci.yaml", Workflow)
        {
            Nodes =
            [
                new("yaml:.github/workflows/ci.yaml#jobs.build", CodeNodeKinds.Section, (6, 13)),
            ],
            Edges =
            [
                new("file:.github/workflows/ci.yaml", "file:.github/actions/deploy/action.yml", CodeEdgeKinds.Imports, 11, CodeConfidences.Exact),
            ],
        },
        new(".github/actions/deploy/action.yml", Action),
    ];

    /// <summary>
    /// Each scalar key anywhere in the file is a key node named by its path with its value as its summary; an anchor's
    /// keys are read through the alias that names it, a merge key's mapping as the merging one's own; and a sequence
    /// item's path holds its place.
    /// </summary>
    [Fact]
    public void EachScalarKeyIsAKeyNodeNamedByItsPath_AnchorsFollowedAndMergeKeysRead()
    {
        var extract = Read("values.yml", Values);
        var merged = Assert.Single(extract.Nodes, node => node.Id == "yaml:values.yml#service.name");

        Assert.Equal(("name", "service.name", "billing"), (merged.Name, merged.Qualified, merged.Summary));

        var anchor = Assert.Single(extract.Nodes, node => node.Id == "yaml:values.yml#base.level");

        Assert.Equal(("level", "info"), (anchor.Name, anchor.Summary));

        var item = Assert.Single(extract.Nodes, node => node.Id == "yaml:values.yml#steps[0].name");

        Assert.Equal("build", item.Summary);
        Assert.DoesNotContain(extract.Nodes, node => node.Qualified.Contains("<<", StringComparison.Ordinal));
        Assert.Empty(extract.Dynamic);
    }

    /// <summary>
    /// A GitHub Actions workflow's jobs are sections over their lines and its steps sections inside them, named by the
    /// name a step gives itself; a <c>uses:</c> naming a local action is an import of its <c>action.yml</c>, and one
    /// naming a marketplace action is not.
    /// </summary>
    [Fact]
    public void AWorkflowsJobsAndStepsAreSections_AndALocalActionIsAnImportOfItsActionYml()
    {
        var extract = Read(".github/workflows/ci.yaml", Workflow);
        var job = Assert.Single(extract.Nodes, node => node.Id == "yaml:.github/workflows/ci.yaml#jobs.build");
        var step = Assert.Single(extract.Nodes, node => node.Id == $"{job.Id}.steps[1]");

        Assert.Equal("deploy", step.Name);
        Assert.Equal(job.Id, step.Parent);
        Assert.Equal([("../../.github/actions/deploy/action.yml", 11, false), ("../../.github/actions/deploy/action.yaml", 11, true)],
            extract.Imports.Select(import => (import.Target, import.Line, import.Fallback)));
    }

    /// <summary>A file that stops reading as YAML has nothing in it read, and the line it stops at marked dynamic; a pubspec is not this extractor's.</summary>
    [Fact]
    public void AFileThatStopsReading_HasNothingReadAndTheLineMarked_AndAPubspecIsAnotherExtractors()
    {
        var extract = Read("broken.yml", "a: [1, 2\nb: }{");

        Assert.Empty(extract.Nodes);
        Assert.Single(extract.Dynamic);

        Assert.Equal("pubspec", CodeGraphExtractors.OfThisBuild.Route("pubspec.yaml", string.Empty).Extractor?.Language);
        Assert.Equal("yaml", CodeGraphExtractors.OfThisBuild.Route("values.yaml", string.Empty).Extractor?.Language);
    }

    private static FileExtract Read(string path, string text) => new YamlExtractor().Extract(new CodeFileInput(path, text));
}
