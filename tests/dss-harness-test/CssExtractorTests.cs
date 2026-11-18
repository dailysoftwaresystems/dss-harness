using DssHarness.Core.Codebase;
using DssHarness.Core.Codebase.Languages;

namespace DssHarness.Tests;

/// <summary>A style sheet, held to the contract's kit, and the rules of its own the kit does not name.</summary>
public sealed class CssExtractorTests : CodeGraphExtractorContractTests<CssExtractor>
{
    private const string Sheet = """
        /* The site's own rules. */
        @import "base.css";
        @use "mixins";
        :root {
          --gap: 1rem;
        }
        .btn, .btn-primary {
          color: #111;
          padding: var(--gap);
        }
        #main my-widget {
          border: none;
        }
        @keyframes slide-in {
          from { opacity: 0; }
          to { opacity: 1; }
        }
        .card {
          &.lifted:hover {
            box-shadow: 1px 1px #000;
          }
        }
        """;

    private const string Scss = """
        $radius: 4px;
        @mixin rounded {
          border-radius: $radius;
        }
        .panel {
          @include rounded;
          @extend .btn;
        }
        """;

    /// <inheritdoc/>
    public override IReadOnlyList<CodeGraphFixture> Fixtures { get; } =
    [
        new("site.css", Sheet)
        {
            Nodes =
            [
                new("css:site.css#--gap", CodeNodeKinds.Member, (5, 5)),
                new("css:site.css#.btn", CodeNodeKinds.Member, (7, 10)),
                new("css:site.css#my-widget", CodeNodeKinds.Member),
                new("css:site.css#slide-in", CodeNodeKinds.Member),
                new("css:site.css#.lifted", CodeNodeKinds.Member),
            ],
            Edges = [new("file:site.css", "file:base.css", CodeEdgeKinds.Imports, 2, CodeConfidences.Exact)],
        },
        new("base.css", "body { margin: 0; }\n"),
        new("mixins.scss", "@mixin rounded {\n  border-radius: 4px;\n}\n"),
        new("styles.sass", ".edge\n  padding: 4px\n"),
        new("styles.less", "@width: 10px;\n.wide { width: @width; }\n"),
    ];

    /// <summary>
    /// Each simple part of each selector is a rule named as the selector writes it, each custom property a constant,
    /// each <c>@keyframes</c> name a rule, a <c>var(--x)</c> a use of the constant, and an <c>@import</c>, <c>@use</c>
    /// and <c>@forward</c> naming a file an import of it; a pseudo-class and a combinator are no parts of their own.
    /// </summary>
    [Fact]
    public void EachSelectorsPartsAreRules_AndItsVariablesAndUsesRead()
    {
        var extract = Read("site.css", Sheet);

        Assert.Equal([".btn", ".btn-primary", "#main", "my-widget", "slide-in", ".card", ".lifted"],
            extract.Nodes.Where(node => node.SubKind == "rule").OrderBy(node => node.Start).Select(node => node.Name));
        Assert.Equal(["--gap"], extract.Nodes.Where(node => node.SubKind == "constant").Select(node => node.Name));
        Assert.DoesNotContain(extract.Nodes, node => node.Name is "hover" or "root");

        Assert.Equal([("--gap", 9)], extract.References.Where(reference => reference.Kind == CodeEdgeKinds.Uses).Select(reference => (reference.Name, reference.Line)));
        Assert.Equal([("base.css", 2), ("mixins.scss", 3), ("_mixins.scss", 3)], extract.Imports.Select(import => (import.Target, import.Line)));
        Assert.Empty(extract.Dynamic);
    }

    /// <summary>SCSS and Less: a variable is a constant, an <c>@include</c> a use of the mixin, an <c>@extend</c> an inheritance of the rule it names.</summary>
    [Fact]
    public void ScssAndLess_MixinsAndExtendsRead()
    {
        var scss = Read("styles.scss", Scss);

        Assert.Equal([("$radius", "constant"), ("rounded", "mixin"), (".panel", "rule")], scss.Nodes.OrderBy(node => node.Start).Select(node => (node.Name, node.SubKind!)));
        Assert.Equal(
            [("$radius", CodeEdgeKinds.Uses, 3), ("rounded", CodeEdgeKinds.Uses, 6), (".btn", CodeEdgeKinds.Inherits, 7)],
            scss.References.OrderBy(reference => reference.Line).Select(reference => (reference.Name, reference.Kind, reference.Line)).ToList());

        var less = Read("styles.less", "@width: 10px;\n.wide { width: @width; }\n");

        Assert.Contains(less.Nodes, node => node.Name == "@width" && node.SubKind == "constant");
    }

    private static FileExtract Read(string path, string text) => new CssExtractor().Extract(new CodeFileInput(path, text));
}
