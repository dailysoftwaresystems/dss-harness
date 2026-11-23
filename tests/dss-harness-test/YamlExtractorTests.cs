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
                new("yaml:.github/workflows/anchored.yml#jobs.test.steps[0]", CodeNodeKinds.Member, (12, 12)),
                new("yaml:.github/workflows/anchored.yml#jobs.copy", CodeNodeKinds.Member, (16, 16)),
                new("yaml:.github/workflows/anchored.yml#description", CodeNodeKinds.Member, (17, 17)),
            ],
            Edges =
            [
                new("yaml:.github/workflows/anchored.yml#jobs.test.steps[0]", "yaml:.github/workflows/anchored.yml#jobs.build.steps[0]", CodeEdgeKinds.Uses, 12, CodeConfidences.Exact),
                new("yaml:.github/workflows/anchored.yml#jobs.copy", "yaml:.github/workflows/anchored.yml#jobs.again", CodeEdgeKinds.Uses, 16, CodeConfidences.Exact),
            ],
        },
    ];

    /// <summary>
    /// A workflow whose steps and jobs alias others - a step of one job named again in another, a job copied whole - each
    /// alias a key and a uses edge to what its anchor names, and a key whose plain value is written on the line after it.
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
    /// Each scalar key anywhere in the file is a key node named by its path with its value as its summary, and each member
    /// that holds others a section inside the one that holds it; an alias is a key whose value is the alias and a merge key
    /// no key at all, each a reference to the node it repeats, never read again where it is written; and a sequence item's
    /// path holds its place.
    /// </summary>
    [Fact]
    public void EachKeyIsANodeNamedByItsPath_AndWhatAnAnchorNamesIsReadOnce()
    {
        var extract = Read("values.yml", Values);

        Assert.Equal(
            [
                ("yaml:values.yml#base", CodeNodeKinds.Section, 1, 3, null, null),
                ("yaml:values.yml#base.level", CodeNodeKinds.Member, 2, 2, "info", "yaml:values.yml#base"),
                ("yaml:values.yml#base.retries", CodeNodeKinds.Member, 3, 3, "3", "yaml:values.yml#base"),
                ("yaml:values.yml#service", CodeNodeKinds.Section, 4, 6, null, null),
                ("yaml:values.yml#service.name", CodeNodeKinds.Member, 6, 6, "billing", "yaml:values.yml#service"),
                ("yaml:values.yml#targets", CodeNodeKinds.Section, 7, 9, null, null),
                ("yaml:values.yml#targets.x64", CodeNodeKinds.Section, 8, 9, null, "yaml:values.yml#targets"),
                ("yaml:values.yml#targets.x64.callingConvention", CodeNodeKinds.Member, 9, 9, "cdecl", "yaml:values.yml#targets.x64"),
                ("yaml:values.yml#steps", CodeNodeKinds.Section, 10, 13, null, null),
                ("yaml:values.yml#steps[0]", CodeNodeKinds.Section, 11, 12, null, "yaml:values.yml#steps"),
                ("yaml:values.yml#steps[0].run", CodeNodeKinds.Member, 11, 11, "dotnet build", "yaml:values.yml#steps[0]"),
                ("yaml:values.yml#steps[0].name", CodeNodeKinds.Member, 12, 12, "build", "yaml:values.yml#steps[0]"),
                ("yaml:values.yml#steps[1]", CodeNodeKinds.Section, 13, 13, null, "yaml:values.yml#steps"),
                ("yaml:values.yml#steps[1].run", CodeNodeKinds.Member, 13, 13, "dotnet test", "yaml:values.yml#steps[1]"),
            ],
            extract.Nodes.Select(node => (node.Id, node.Kind, node.Start, node.End, node.Summary, node.Parent)));
        Assert.Equal([("yaml:values.yml#service", "base", 5, "yaml:values.yml#base")], References(extract));
        Assert.Empty(extract.Dynamic);
    }

    /// <summary>
    /// A GitHub Actions workflow's jobs are sections over their lines and its steps sections inside them, named by the
    /// name a step gives itself, else by its place; a <c>uses:</c> naming a local action is an import of its
    /// <c>action.yml</c>, and one naming a marketplace action is not.
    /// </summary>
    [Fact]
    public void AWorkflowsJobsAndStepsAreSections_AndALocalActionIsAnImportOfItsActionYml()
    {
        var extract = Read(".github/workflows/ci.yaml", Workflow);
        var job = Assert.Single(extract.Nodes, node => node.Id == "yaml:.github/workflows/ci.yaml#jobs.build");
        var steps = Assert.Single(extract.Nodes, node => node.Id == $"{job.Id}.steps");

        Assert.Equal(
            [("step 1", 9, 9, steps.Id.Whole()), ("deploy", 10, 13, steps.Id.Whole())],
            extract.Nodes.Where(node => node.Parent == steps.Id).Select(node => (node.Name, node.Start, node.End, node.Parent?.Whole())));
        Assert.Equal((6, 13, "yaml:.github/workflows/ci.yaml#jobs"), (job.Start, job.End, job.Parent?.Whole()));
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
            extract.Nodes.Select(node => (node.Id.Whole(), node.Summary)));
        Assert.Equal([4], extract.Dynamic.Select(mark => mark.Line));
        Assert.StartsWith("the file stops reading as YAML here (", Assert.Single(extract.Dynamic).Why, StringComparison.Ordinal);

        // A fault the reader says no place of is said past the last place it read.
        var placeless = Read("tag.yml", "x: 1\ny: !<> b\nz: 3\n");

        Assert.Equal(["yaml:tag.yml#x"], placeless.Nodes.Select(node => node.Id.Whole()));
        Assert.Equal([new ExtractDynamic(2, "the file stops reading as YAML past here (Tag value must not be empty. (Parameter 'value')), and nothing after it is read", DataNodes.Stop)], placeless.Dynamic);

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
    /// Aliases that repeat one another - a few hundred bytes standing for ten million keys - are each a reference to the
    /// node their anchor names: what each anchor names is read once, where it is written, so the file is as many nodes as it
    /// writes, read in a moment, and marked nowhere.
    /// </summary>
    [Fact]
    public void AliasesThatRepeatEachOther_AreEachAReferenceToTheirAnchor_ReadOnce()
    {
        var text = new System.Text.StringBuilder("a: &a [1, 1, 1, 1, 1, 1, 1, 1, 1, 1]\n");
        var names = new[] { "a", "b", "c", "d", "e", "f", "g" };

        for (var at = 1; at < names.Length; at++)
        {
            text.Append($"{names[at]}: &{names[at]} [{string.Join(", ", Enumerable.Repeat($"*{names[at - 1]}", 10))}]\n");
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var extract = Read("bomb.yml", text.ToString());

        // Each list a section and each of its ten items a key: the first's of 1, every other's an alias of the list before.
        Assert.Equal(
            [.. names.SelectMany(name => new[] { name }.Concat(Enumerable.Range(0, 10).Select(item => $"{name}[{item}]")))],
            extract.Nodes.Select(node => { var qualified = node.Qualified.Whole(); return qualified[(qualified.IndexOf('#', StringComparison.Ordinal) + 1)..]; }));
        Assert.Equal(
            [.. names.Skip(1).SelectMany((name, at) => Enumerable.Range(0, 10).Select(item => ($"yaml:bomb.yml#{name}[{item}]", names[at], at + 2, $"yaml:bomb.yml#{names[at]}")))],
            References(extract));
        Assert.Empty(extract.Dynamic);

        // As below: the bound is the matrix's slowest runner, and what a wrong read takes is far past it.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"the file took {watch.Elapsed} to read");
    }

    /// <summary>
    /// An alias of one wide anchor written again and again is a reference each time, the anchor's fifty thousand items
    /// never read again: a list of a hundred thousand aliases of it is a key and a reference each, read in a moment.
    /// </summary>
    [Fact]
    public void AnAliasOfAWideAnchorWrittenAgainAndAgain_IsAReferenceEachTime()
    {
        const int Wide = 50_001;
        const int Aliases = 100_000;
        var text = $"a: &a [{string.Join(", ", Enumerable.Repeat("[]", Wide))}]\nb: [{string.Join(", ", Enumerable.Repeat("*a", Aliases))}]\n";
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var extract = Read("wide.yml", text);

        Assert.Equal((1 + Wide + 1 + Aliases, Aliases), (extract.Nodes.Count, extract.References.Count));
        Assert.All(extract.References, reference => Assert.Equal(new ExtractQualifier(CodeQualifierKinds.Own, "yaml:wide.yml#a"), reference.Qualifier));
        Assert.Equal(("*a", "yaml:wide.yml#b"), (extract.Nodes[^1].Summary, extract.Nodes[^1].Parent));
        Assert.Empty(extract.Dynamic);

        // The slowest runner of the matrix - a shared two-core macOS under the suite's own load - reads this in twenty
        // seconds; a read that is wrong, the anchor read again for each of its aliases, is minutes past any bound here.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"the file took {watch.Elapsed} to read");
    }

    /// <summary>
    /// A large CI file of a template every job merges - with a second mapping that shares two of its names, and a key of
    /// the template each job writes itself - is each job's own keys and a reference to each mapping it merges, in order: the
    /// template's keys read once, where it is written, and the file marked nowhere.
    /// </summary>
    [Fact]
    public void ALargeFileOfMerges_IsAReferenceToEachMappingMerged_TheTemplateReadOnce()
    {
        const int Jobs = 2500;
        var text = new System.Text.StringBuilder(".defaults: &defaults\n");

        for (var at = 0; at < 25; at++)
        {
            text.Append($"  k{at}: v{at}\n");
        }

        text.Append(".docker: &docker\n  k23: x\n  k24: y\n  image: dotnet\n  services: db\n  tags: linux\n");

        for (var at = 0; at < Jobs; at++)
        {
            text.Append($"job{at}:\n  <<: [*defaults, *docker]\n  k0: own\n  script: run {at}\n");
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var extract = Read(".gitlab-ci.yml", text.ToString());

        // The two templates and their keys, then each job's section and the two keys it writes itself.
        Assert.Equal(1 + 25 + 1 + 5 + (Jobs * 3), extract.Nodes.Count);
        Assert.Equal(
            [.. Enumerable.Range(0, Jobs).SelectMany(at => new[] { ($"yaml:.gitlab-ci.yml#job{at}", "defaults", 34 + (at * 4), "yaml:.gitlab-ci.yml#.defaults"), ($"yaml:.gitlab-ci.yml#job{at}", "docker", 34 + (at * 4), "yaml:.gitlab-ci.yml#.docker") })],
            References(extract));
        Assert.Empty(extract.Dynamic);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"the file took {watch.Elapsed} to read");
    }

    /// <summary>
    /// Aliases that each nest the one before - a chain thousands long - are each a reference to the list before, read on
    /// a stack of the reader's own; and a file that nests thousands of levels deep is read whole on a thread whose stack is
    /// a fraction of what reading it by recursion would take.
    /// </summary>
    [Fact]
    public void AChainOfAliases_AndAFileNestedThousandsDeep_AreReadWhole()
    {
        var text = new System.Text.StringBuilder("a0: &a0 [1]\n");

        for (var at = 1; at <= 5000; at++)
        {
            text.Append($"a{at}: &a{at} [*a{at - 1}]\n");
        }

        var chain = Read("chain.yml", text.ToString());

        Assert.Equal(5001 * 2, chain.Nodes.Count);
        Assert.Equal([.. Enumerable.Range(1, 5000).Select(at => ($"yaml:chain.yml#a{at}[0]", $"a{at - 1}", at + 1, $"yaml:chain.yml#a{at - 1}"))], References(chain));
        Assert.Empty(chain.Dynamic);
        Assert.Null(chain.WithDigests(text.ToString()).Problem("yaml", CodeFileText.Lines(text.ToString()).Count));

        const int Depth = 3000;
        var deep = string.Concat(Enumerable.Repeat("{a: ", Depth)) + "1" + new string('}', Depth);
        FileExtract? nested = null;
        var thread = new Thread(() => nested = Read("deep.yml", deep), 256 * 1024);

        thread.Start();
        thread.Join();

        Assert.Equal((Depth, Depth - 1), (nested!.Nodes.Count, nested.Nodes.Count(node => node.Kind == CodeNodeKinds.Section)));
        Assert.Equal((string.Join('.', Enumerable.Repeat("a", Depth)), "1"), (nested.Nodes[^1].Qualified, nested.Nodes[^1].Summary));
        Assert.Empty(nested.Dynamic);
    }

    /// <summary>
    /// An alias is a key on its own line, inside its own section, whose value is the alias; a workflow whose jobs alias
    /// each other is its written jobs' sections and a key for each alias, the anchor's steps named once, where they are
    /// written - an extract the core holds.
    /// </summary>
    [Fact]
    public void AnAlias_IsAKeyOnItsOwnLine_InsideItsSection()
    {
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
            [("jobs", 2, 7), ("build", 3, 6), ("steps", 5, 6), ("step 1", 6, 6)],
            workflow.Nodes.Where(node => node.Kind == CodeNodeKinds.Section).Select(node => (node.Name, node.Start, node.End)));
        Assert.Equal(
            ("*job", 7, "yaml:.github/workflows/twice.yml#jobs"),
            Assert.Single(workflow.Nodes, node => node.Id == "yaml:.github/workflows/twice.yml#jobs.test") is var test ? (test.Summary, test.Start, test.Parent) : default);
        Assert.Equal([("yaml:.github/workflows/twice.yml#jobs.test", "job", 7, "yaml:.github/workflows/twice.yml#jobs.build")], References(workflow));
        Assert.Null(workflow.WithDigests(Workflow).Problem("yaml", CodeFileText.Lines(Workflow).Count));
    }

    /// <summary>
    /// A file of several documents is each document a section by its place - a Kubernetes file's Deployment and its
    /// Service each its keys - and a merge key of a list is a reference to each mapping it merges, in order, a mapping
    /// written in the list read where it is, at <c>&lt;&lt;</c> and its place.
    /// </summary>
    [Fact]
    public void EachDocumentIsASectionByItsPlace_AndAMergeOfAListRefersToEach()
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
            documents.Nodes.Select(node => (node.Id.Whole(), node.Start, node.End)));

        var merged = Read("merge.yml", "a: &a { timeout: 1, retries: 2 }\nb: &b { timeout: 3, level: x }\nc:\n  <<: [*a, *b, { own: 1 }]\n  retries: 9\n");

        Assert.Equal(
            [("c", null), ("c.<<", null), ("c.<<[2]", null), ("c.<<[2].own", "1"), ("c.retries", "9")],
            merged.Nodes.Where(node => node.Signature.StartsWith('c')).Select(node => (node.Signature, node.Summary)));
        Assert.Equal(
            [("yaml:merge.yml#c", "a", 4, "yaml:merge.yml#a"), ("yaml:merge.yml#c", "b", 4, "yaml:merge.yml#b"), ("yaml:merge.yml#c", "<<", 4, "yaml:merge.yml#c.<<[2]")],
            References(merged));
    }

    /// <summary>
    /// A job's <c>uses:</c> naming a reusable workflow of the repository is an import of that workflow's file - no
    /// <c>action.yml</c> - and a step's naming a local action an import of its <c>action.yml</c>; a job or a step reached
    /// through an alias or a merge is read once, on the line its <c>uses:</c> is written on, and a step is named where it is
    /// written in a job's steps alone.
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

        var anchored = Read(".github/workflows/anchored.yml", """
            x-deploy: &deploy
              uses: ./.github/actions/deploy
            jobs:
              one:
                steps:
                  - *deploy
                  - <<: *deploy
                    name: again
              two:
                steps:
                  - *deploy
            """);

        Assert.Equal(
            [("../../.github/actions/deploy/action.yml", 2, false), ("../../.github/actions/deploy/action.yaml", 2, true)],
            anchored.Imports.Select(import => (import.Target, import.Line, import.Fallback)));

        // A step is named where it is written in a job's steps: what an alias of it names keeps the name of its own place.
        Assert.Equal(
            ["x-deploy", "jobs", "one", "steps", "again", "two", "steps"],
            anchored.Nodes.Where(node => node.Kind == CodeNodeKinds.Section).Select(node => node.Name));
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

    /// <summary>Each reference an extract makes, by where it is made from, the name it repeats, its line and the node of its own file it names.</summary>
    private static IEnumerable<(string From, string Name, int Line, string? To)> References(FileExtract extract)
        => extract.References.Select(reference => (reference.From.Whole(), reference.Name, reference.Line, reference.Qualifier?.Kind == CodeQualifierKinds.Own ? reference.Qualifier.Name?.Whole() : null));
}
