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
        new(".github/workflows/anchored.yml", Anchored)
        {
            Nodes =
            [
                new("yaml:.github/workflows/anchored.yml#jobs.build.steps[0]", CodeNodeKinds.Section, (6, 7)),
                new("yaml:.github/workflows/anchored.yml#jobs.test", CodeNodeKinds.Section, (9, 13)),
                new("yaml:.github/workflows/anchored.yml#jobs.test.steps[0]", CodeNodeKinds.Section, (12, 12)),
                new("yaml:.github/workflows/anchored.yml#jobs.test.steps[0].uses", CodeNodeKinds.Member, (12, 12)),
                new("yaml:.github/workflows/anchored.yml#jobs.copy", CodeNodeKinds.Section, (16, 16)),
                new("yaml:.github/workflows/anchored.yml#description", CodeNodeKinds.Member, (17, 17)),
            ],
        },
    ];

    /// <summary>
    /// A workflow whose steps and jobs alias others - a step of one job written again in another, a job copied whole -
    /// and a key whose plain value is written on the line after it.
    /// </summary>
    private const string Anchored = """
        on: push
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - &checkout
                uses: actions/checkout@v4
              - run: make
          test:
            runs-on: ubuntu-latest
            steps:
              - *checkout
              - run: make test
          again: &again
            runs-on: ubuntu-latest
          copy: *again
        description:
          the text on the next line
        """;

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

    /// <summary>
    /// A file that stops reading as YAML keeps what it read before the place it stops - a template's <c>{{ }}</c> among
    /// such places - and that line is marked dynamic with the reader's reason; an empty file, or one of comments, stopped
    /// nowhere; a pubspec is not this extractor's.
    /// </summary>
    [Fact]
    public void AFileThatStopsReading_KeepsWhatCameBefore_AndTheLineMarked_AndAPubspecIsAnotherExtractors()
    {
        var extract = Read("broken.yml", "first: 1\nsecond:\n  third: 3\nfourth: @{{ .Values.x }}\nfifth: 5\n");

        Assert.Equal(
            [("yaml:broken.yml#first", "1"), ("yaml:broken.yml#second", null), ("yaml:broken.yml#second.third", "3")],
            extract.Nodes.Select(node => (node.Id, node.Summary)));
        Assert.Equal([4], extract.Dynamic.Select(mark => mark.Line));
        Assert.StartsWith("the file stops reading as YAML here (", Assert.Single(extract.Dynamic).Why, StringComparison.Ordinal);

        // A fault the reader says no place of is said past the last place it read.
        var placeless = Read("tag.yml", "x: 1\ny: !<> b\nz: 3\n");

        Assert.Equal(["yaml:tag.yml#x"], placeless.Nodes.Select(node => node.Id));
        Assert.Equal([new ExtractDynamic(2, "the file stops reading as YAML past here (Tag value must not be empty. (Parameter 'value')), and nothing after it is read")], placeless.Dynamic);

        foreach (var text in new[] { string.Empty, "# a file of comments\n\n# and more\n", "---\n...\n" })
        {
            var empty = Read("empty.yml", text);

            Assert.Empty(empty.Nodes);
            Assert.Empty(empty.Dynamic);
        }

        Assert.Equal("pubspec", CodeGraphExtractors.OfThisBuild.Route("pubspec.yaml", string.Empty).Extractor?.Language);
        Assert.Equal("yaml", CodeGraphExtractors.OfThisBuild.Route("values.yaml", string.Empty).Extractor?.Language);
    }

    /// <summary>
    /// Aliases that repeat one another - a few hundred bytes standing for ten million keys - are read until the extract
    /// keeps <see cref="FileExtract.MostKeys"/> nodes, and every key past them is counted once a node, never walked again;
    /// aliases of what holds no key are walked again at most <see cref="DataNodes.MostRepeated"/> nodes, the line marked
    /// where they stop being read. Each file is read in a moment.
    /// </summary>
    [Fact]
    public void AliasesThatRepeatEachOther_AreReadToABound_AndTheRestCounted()
    {
        static string Bomb(string first)
        {
            var text = new System.Text.StringBuilder($"a: &a {first}\n");

            foreach (var (name, before) in new[] { ("b", "a"), ("c", "b"), ("d", "c"), ("e", "d"), ("f", "e"), ("g", "f") })
            {
                text.Append($"{name}: &{name} [{string.Join(", ", Enumerable.Repeat($"*{before}", 10))}]\n");
            }

            return text.ToString();
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var keyed = Read("bomb.yml", Bomb("[1, 1, 1, 1, 1, 1, 1, 1, 1, 1]"));

        // Every key the file stands for is a node or counted - 10 + 100 + ... + 10^7 of them - beside the sections a to d,
        // which the extract keeps before it is full.
        var keys = Enumerable.Range(1, 7).Sum(level => (long)Math.Pow(10, level));

        Assert.Equal((FileExtract.MostKeys, keys + 4 - FileExtract.MostKeys), (keyed.Nodes.Count, (long)keyed.KeysBeyond));
        Assert.Empty(keyed.Dynamic);

        var hollow = Read("hollow.yml", Bomb("[]"));

        Assert.Equal(["a", "b", "c", "d", "e", "f", "g"], hollow.Nodes.Select(node => node.Name));
        Assert.Equal(0, hollow.KeysBeyond);
        Assert.Equal([new ExtractDynamic(6, $"the file's aliases repeat more than {DataNodes.MostRepeated} nodes, and what they repeat from here on is not read")], hollow.Dynamic);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"the files took {watch.Elapsed} to read");
    }

    /// <summary>
    /// Merge keys that merge one large mapping again and again - each merge reading every key of it, the ones a key before
    /// shadows among them - spend the file's budget of what it repeats as aliases do: past
    /// <see cref="DataNodes.MostRepeated"/> nodes no merge reads more, and the line it stops at is marked.
    /// </summary>
    [Fact]
    public void MergesOfOneMappingAgainAndAgain_SpendTheBudgetOfWhatTheFileRepeats()
    {
        var text = new System.Text.StringBuilder($"a: &a {{ {string.Join(", ", Enumerable.Range(0, 100).Select(at => $"k{at}: 1"))} }}\n");

        for (var at = 0; at < 10; at++)
        {
            text.Append($"m{at}: {{ <<: [{string.Join(", ", Enumerable.Repeat("*a", 64))}] }}\n");
        }

        var extract = Read("merges.yml", text.ToString());

        // Each mapping reads its 64 merges' 6,400 keys and walks the 100 they give: the eighth, m7 on line 9, passes the
        // budget, keeping the keys its first merge gave; m8 and m9 merge none.
        Assert.Equal([new ExtractDynamic(9, $"the file's aliases repeat more than {DataNodes.MostRepeated} nodes, and what they repeat from here on is not read")], extract.Dynamic);
        Assert.Equal(
            [100, 100, 100, 100, 100, 100, 100, 100, 0, 0],
            Enumerable.Range(0, 10).Select(at => extract.Nodes.Count(node => node.Qualified.StartsWith($"m{at}.", StringComparison.Ordinal))));
    }

    /// <summary>
    /// Aliases that each nest the one before deeper than any file is read are followed no deeper than a file's text is:
    /// the alias whose node would nest past <see cref="DataNodes.MostNesting"/> levels where it is written is not followed,
    /// the line it is on marked, and nothing recurses as deep as the chain.
    /// </summary>
    [Fact]
    public void AliasesNestingEachOtherPastTheBound_AreNotFollowedPastIt()
    {
        var text = new System.Text.StringBuilder("a0: &a0 [1]\n");

        for (var at = 1; at <= 5000; at++)
        {
            text.Append($"a{at}: &a{at} [*a{at - 1}]\n");
        }

        var extract = Read("chain.yml", text.ToString());

        // a{k} nests k + 1 lists, and its alias of a{k - 1} two deep: the first not followed is a62's, in a63 on line 64 -
        // which then nests one list, so the chain begins again, and one in every 63 is not followed.
        Assert.Equal(
            new ExtractDynamic(DataNodes.MostNesting, $"the alias '*a{DataNodes.MostNesting - 2}' repeats here what nests more than {DataNodes.MostNesting} mappings and lists deep, and none of it is read"),
            extract.Dynamic[0]);
        Assert.Equal(5000 / (DataNodes.MostNesting - 1), extract.Dynamic.Count);
        Assert.Null(extract.WithDigests(CodeFileText.Lines(text.ToString())).Problem("yaml", CodeFileText.Lines(text.ToString()).Count));
    }

    /// <summary>
    /// A key reached through an alias is on the alias's line, where this place writes it - inside its own section, as the
    /// anchor's line would not be - and so is a key a merge key brings in through one; a workflow whose jobs alias each
    /// other is an extract the core holds.
    /// </summary>
    [Fact]
    public void AKeyReachedThroughAnAlias_IsOnTheAliassLine_InsideItsSection()
    {
        var extract = Read("values.yml", Values);

        Assert.Equal(
            [("base", 1, 3), ("service", 4, 6), ("targets", 7, 9), ("steps", 10, 13)],
            extract.Nodes.Where(node => node.Kind == CodeNodeKinds.Section).Select(node => (node.Name, node.Start, node.End)));
        Assert.Equal(
            [("service.name", 6), ("service.level", 5), ("service.retries", 5)],
            extract.Nodes.Where(node => node.Qualified.StartsWith("service.", StringComparison.Ordinal)).Select(node => (node.Qualified, node.Start)));

        const string Workflow = """
            on: push
            jobs:
              build: &job
                runs-on: ubuntu-latest
                steps:
                  - run: make
              test: *job
            """;
        var workflow = Read(".github/workflows/twice.yml", Workflow);

        Assert.Equal(
            [("jobs", 2, 7), ("build", 3, 6), ("step 1", 6, 6), ("test", 7, 7), ("step 1", 7, 7)],
            workflow.Nodes.Where(node => node.Kind == CodeNodeKinds.Section).Select(node => (node.Name, node.Start, node.End)));
        Assert.Null(workflow.WithDigests(CodeFileText.Lines(Workflow)).Problem("yaml", CodeFileText.Lines(Workflow).Count));
    }

    /// <summary>
    /// A file of several documents is each document a section by its place - a Kubernetes file's Deployment and its
    /// Service each its keys - and a merge key of a list merges each mapping of it at the merging path, the mapping's own
    /// key winning and an earlier merged one over a later.
    /// </summary>
    [Fact]
    public void EachDocumentIsASectionByItsPlace_AndAMergeOfAListMergesEach()
    {
        var documents = Read("deploy.yaml", "kind: Deployment\nreplicas: 2\n---\nkind: Service\nport: 80\n");

        Assert.Equal(
            [
                ("yaml:deploy.yaml#[0]", 1, 2),
                ("yaml:deploy.yaml#[0].kind", 1, 1),
                ("yaml:deploy.yaml#[0].replicas", 2, 2),
                ("yaml:deploy.yaml#[1]", 4, 5),
                ("yaml:deploy.yaml#[1].kind", 4, 4),
                ("yaml:deploy.yaml#[1].port", 5, 5),
            ],
            documents.Nodes.Select(node => (node.Id, node.Start, node.End)));

        var merged = Read("merge.yml", "a: &a { timeout: 1, retries: 2 }\nb: &b { timeout: 3, level: x }\nc:\n  <<: [*a, *b]\n  retries: 9\n");

        Assert.Equal(
            [("c.retries", "9"), ("c.timeout", "1"), ("c.level", "x")],
            merged.Nodes.Where(node => node.Qualified.StartsWith("c.", StringComparison.Ordinal)).Select(node => (node.Qualified, node.Summary)));
    }

    /// <summary>
    /// A job's <c>uses:</c> naming a reusable workflow of the repository is an import of that workflow's file - no
    /// <c>action.yml</c> - and a step's naming a local action an import of its <c>action.yml</c>.
    /// </summary>
    [Fact]
    public void AJobsUses_IsAnImportOfTheReusableWorkflowsFile()
    {
        var extract = Read(".github/workflows/ci.yml", """
            on: push
            jobs:
              call:
                uses: ./.github/workflows/build.yml
              remote:
                uses: octo/repo/.github/workflows/x.yml@v1
            """);

        Assert.Equal([("../../.github/workflows/build.yml", 4, false)], extract.Imports.Select(import => (import.Target, import.Line, import.Fallback)));
    }

    /// <summary>
    /// The lines are the core's - CR, LF and CR LF alone - whatever the YAML reader breaks a line at: a quoted value
    /// holding a line or paragraph separator leaves every key after it on the line the file has it on.
    /// </summary>
    [Fact]
    public void TheLinesAreTheCores_ThoughAValueHoldsALineSeparator()
    {
        // Each separator ends a line of the quoted value for the YAML reader, which reads the value on as it goes on, a space
        // in.
        var extract = Read("sep.yml", "a: \"x\u2028 y\u2029 z\u0085 w\"\nb: 1\n");

        Assert.Equal([("a", 1), ("b", 2)], extract.Nodes.Select(node => (node.Name, node.Start)));
    }

    private static FileExtract Read(string path, string text) => new YamlExtractor().Extract(new CodeFileInput(path, text));
}
