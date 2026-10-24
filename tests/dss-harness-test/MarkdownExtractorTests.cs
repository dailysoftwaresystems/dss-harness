using DssHarness.Core.Codebase;
using DssHarness.Core.Codebase.Languages;

namespace DssHarness.Tests;

/// <summary>Markdown's outline, held to the contract's kit, and the rules of its own the kit does not name.</summary>
public sealed class MarkdownExtractorTests : CodeGraphExtractorContractTests<MarkdownExtractor>
{
    private const string Guide = """
        Guide
        =====

        The guide says how to use it. More text follows.

        ## Install

        Run the tool.

        ```sh
        # not a heading
        ```

        ## Usage

        ### Options

        Pass flags.

        ### Options

        Again.

        Notes
        -----

        Last words.

        """;

    private const string Readme = """
        # Title #

        What it is!  And more.

        #

            # indented, so code

        ~~~~
        ## fenced
        ```
        still fenced
        ~~~~~

        ## After the fence ##
        """;

    private const string Notes = """
        ---
        title: Notes
        # not a heading
        ---

        Intro
        =====

        - item
        ---

        > quoted
        ---

        | a | b |
        ---

        ```one `two`
        ---

        ```text
        ## fenced
        ```

        ## End
        """;

    /// <inheritdoc/>
    public override IReadOnlyList<CodeGraphFixture> Fixtures { get; } =
    [
        new("docs/guide.md", Guide)
        {
            Nodes =
            [
                new("markdown:docs/guide.md#Guide", CodeNodeKinds.Section, (1, 27)),
                new("markdown:docs/guide.md#Guide/Install", CodeNodeKinds.Section, (6, 13)),
                new("markdown:docs/guide.md#Guide/Usage", CodeNodeKinds.Section, (14, 23)),
                new("markdown:docs/guide.md#Guide/Usage/Options", CodeNodeKinds.Section, (16, 19)),
                new("markdown:docs/guide.md#Guide/Usage/Options~2", CodeNodeKinds.Section, (20, 23)),
                new("markdown:docs/guide.md#Guide/Notes", CodeNodeKinds.Section, (24, 27)),
            ],
            Edges =
            [
                new("file:docs/guide.md", "markdown:docs/guide.md#Guide", CodeEdgeKinds.Contains, 1, CodeConfidences.Exact),
                new("markdown:docs/guide.md#Guide", "markdown:docs/guide.md#Guide/Usage", CodeEdgeKinds.Contains, 14, CodeConfidences.Exact),
                new("markdown:docs/guide.md#Guide/Usage", "markdown:docs/guide.md#Guide/Usage/Options~2", CodeEdgeKinds.Contains, 20, CodeConfidences.Exact),
            ],
        },
        new("README.md", Readme)
        {
            Nodes =
            [
                new("markdown:README.md#Title", CodeNodeKinds.Section, (1, 4)),
                new("markdown:README.md#(untitled)", CodeNodeKinds.Section, (5, 15)),
                new("markdown:README.md#(untitled)/After the fence", CodeNodeKinds.Section, (15, 15)),
            ],
        },
        new("docs/notes.md", Notes)
        {
            Nodes =
            [
                new("markdown:docs/notes.md#Intro", CodeNodeKinds.Section, (6, 25)),
                new("markdown:docs/notes.md#Intro/```one `two`", CodeNodeKinds.Section, (18, 24)),
                new("markdown:docs/notes.md#Intro/End", CodeNodeKinds.Section, (25, 25)),
            ],
        },
    ];

    /// <summary>
    /// A section's level, its parent, where its body begins and its summary - the first sentence of its first text - are
    /// what an agent is served before the section itself; a heading inside a fence or indented as code is none.
    /// </summary>
    [Fact]
    public void ASection_KnowsItsLevel_ItsParent_ItsBody_AndItsSummary()
    {
        var guide = Extractor.Extract(new CodeFileInput("docs/guide.md", Guide)).Nodes.ToDictionary(node => node.Id);
        var readme = Extractor.Extract(new CodeFileInput("README.md", Readme)).Nodes;

        var top = guide["markdown:docs/guide.md#Guide"];
        Assert.Equal(("h1", null, 3, "The guide says how to use it.", "Guide", "docs/guide.md#Guide"), (top.SubKind, top.Parent, top.BodyStart, top.Summary, top.Signature, top.Qualified));

        var install = guide["markdown:docs/guide.md#Guide/Install"];
        Assert.Equal(("h2", top.Id, 7, "Run the tool.", "## Install"), (install.SubKind, install.Parent, install.BodyStart, install.Summary, install.Signature));

        // A section whose first line under it is a heading of its own says nothing of itself.
        Assert.Null(guide["markdown:docs/guide.md#Guide/Usage"].Summary);
        Assert.Equal("h3", guide["markdown:docs/guide.md#Guide/Usage/Options~2"].SubKind);
        Assert.Equal("Options", guide["markdown:docs/guide.md#Guide/Usage/Options~2"].Name);

        Assert.Equal(["Title", "(untitled)", "After the fence"], readme.Select(node => node.Name));
        Assert.Equal("What it is!", readme[0].Summary);
        Assert.Equal("# Title #", readme[0].Signature);
    }

    /// <summary>
    /// Front matter is data, and no heading is read in it; a list item, a quote or a table's row under a rule is no heading,
    /// and a section's summary is its first text; a backtick line whose info string holds a backtick opens no fence, and
    /// is a heading's title under an underline.
    /// </summary>
    [Fact]
    public void FrontMatter_ListsQuotesAndTablesUnderARule_AndBacktickSpans_AreNoHeadingsOrFences()
    {
        var notes = Extractor.Extract(new CodeFileInput("docs/notes.md", Notes)).Nodes;

        Assert.Equal(["Intro", "```one `two`", "End"], notes.Select(node => node.Name));
        Assert.Equal(("h1", "- item"), (notes[0].SubKind, notes[0].Summary));
        Assert.Equal(("h2", notes[0].Id), (notes[1].SubKind, notes[1].Parent));

        // A rule right under a heading's underline is a rule, never a heading of the underline; nor is one under a rule.
        Assert.Equal(["Title"], Extractor.Extract(new CodeFileInput("rule.md", "Title\n===\n---\n")).Nodes.Select(node => node.Name));
        Assert.Equal(["Top"], Extractor.Extract(new CodeFileInput("rules.md", "# Top\n\n---\n---\n\n***\n---\n\n___\n---\n\n* * *\n---\n")).Nodes.Select(node => node.Name));

        // A first line --- with no closing one is no front matter: a rule, under which the document is read.
        Assert.Equal(["Top"], Extractor.Extract(new CodeFileInput("open.md", "---\n# Top\n")).Nodes.Select(node => node.Name));
        Assert.Equal(["Top"], Extractor.Extract(new CodeFileInput("dots.md", "---\na: 1\n...\n# Top\n")).Nodes.Select(node => node.Name));
    }

    /// <summary>A heading longer than a signature holds is cut at a word, with an ellipsis, and so is a summary.</summary>
    [Fact]
    public void ALongHeadingAndSummary_AreCutAtAWord_WithAnEllipsis()
    {
        var words = string.Join(' ', Enumerable.Repeat("word", 80));
        var node = Assert.Single(Extractor.Extract(new CodeFileInput("long.md", $"# {words}\n\n{words}\n")).Nodes);

        Assert.True(node.Signature.Length <= FileExtract.MostSignature);
        Assert.EndsWith("word…", node.Signature, StringComparison.Ordinal);
        Assert.True(node.Summary!.Length <= FileExtract.MostSummary);
        Assert.EndsWith("word…", node.Summary, StringComparison.Ordinal);

        // A cut with no word to fall back to never splits a character written as a surrogate pair.
        var faces = string.Concat(Enumerable.Repeat("\U0001F600", 200));
        var text = $"# {faces}\n\n{faces}\n";
        var input = new CodeFileInput("faces.md", text);
        var extract = Extractor.Extract(input);
        var face = Assert.Single(extract.Nodes);

        Assert.Null(extract.WithDigests(input.Lines).Problem(Extractor.Language, input.Lines.Count));
        Assert.True(face.Signature.Length <= FileExtract.MostSignature);
        Assert.EndsWith("\U0001F600…", face.Signature, StringComparison.Ordinal);
        Assert.EndsWith("\U0001F600…", face.Summary, StringComparison.Ordinal);
    }
}
