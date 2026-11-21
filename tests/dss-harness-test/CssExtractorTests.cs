using System.Diagnostics;
using DssHarness.Core.Codebase;
using DssHarness.Core.Codebase.Languages;
using DssHarness.Core.Codebase.Languages.Scanning;

namespace DssHarness.Tests;

/// <summary>A style sheet, held to the contract's kit, and the rules of its own the kit does not name.</summary>
public sealed class CssExtractorTests : CodeGraphExtractorContractTests<CssExtractor>
{
    private const string Sheet = """
        /* The site's own rules. */
        @import "base.css";
        @import url(print.css) print;
        :root {
          --gap: 1rem;
        }
        /** The primary buttons. */
        .btn,
        .btn-primary {
          color: #111;
          padding: var(--gap);
        }
        #main my-widget {
          border: none;
          background: url(//cdn.example.com/a.png);
        }
        @keyframes slide-in {
          from { opacity: 0; }
          to { opacity: 1; }
        }
        @media (min-width: 768px) {
          .card:not(.flat) {
            content: "it's // no comment";
          }
        }
        :is(.lead, .intro) p {
          margin: 0;
        }
        .sm\:hidden {
          display: none;
        }
        @font-face {
          font-family: "Inter";
          src: url("inter.woff2") format("woff2");
        }
        @property --angle {
          syntax: "<angle>";
          inherits: false;
          initial-value: 0deg;
        }
        .after { rotate: var(--angle); }
        """;

    private const string Scss = """
        @use "sass:math";
        @use "theme/colors" as c;
        @forward "src/list" hide list-reset;
        $radius: 4px !default;
        %raised {
          box-shadow: 0 1px 2px #000;
        }
        @mixin rounded($r: $radius) {
          border-radius: $r;
        }
        @function half($n) {
          @return math.div($n, 2);
        }
        .panel {
          @include rounded;
          @extend %raised;
          color: c.$primary;
          &__title {
            margin: half(4px);
          }
          &:hover {
            color: red;
          }
          @media (min-width: 600px) {
            .wide { width: 100%; }
          }
          padding: 0;
        }
        @each $size in $sizes {
          .col-#{$size} { width: $size; }
        }
        """;

    private const string Sass = """
        // The sheet's own
          rules, in the indented syntax.
        $gap: 4px
        =bevel($r: 2px)
          border-radius: $r
        .edge,
        .rim
          padding: $gap // a trailing comment
          .inner
            +bevel
            margin: 0
        .other
          color: red
        """;

    private const string Less = """
        @import (reference) "mixins";
        @width: 10px;
        .rounded(@r: 2px) {
          border-radius: @r;
        }
        .wide {
          width: @width;
          .rounded();
          &:extend(.base all);
        }
        .guard when (@mode = dark) {
          color: black;
        }
        .@{prefix}-btn {
          color: red;
        }
        """;

    private const string Module = """
        .btn-base { color: red; }
        .btn-primary { color: blue; }
        .btn-large { composes: btn-base; }
        """;

    /// <inheritdoc/>
    public override IReadOnlyList<CodeGraphFixture> Fixtures { get; } =
    [
        new("site.css", Sheet)
        {
            Nodes =
            [
                new("css:site.css#--gap", CodeNodeKinds.Member, (5, 5)),
                new("css:site.css#.btn", CodeNodeKinds.Member, (8, 12)),
                new("css:site.css#my-widget", CodeNodeKinds.Member, (13, 16)),
                new("css:site.css#@keyframes slide-in", CodeNodeKinds.Member, (17, 20)),
                new("css:site.css#.card", CodeNodeKinds.Member, (22, 24)),
                new("css:site.css#.after", CodeNodeKinds.Member, (41, 41)),
            ],
            Edges =
            [
                new("file:site.css", "file:base.css", CodeEdgeKinds.Imports, 2, CodeConfidences.Exact),
                new("css:site.css#.btn", "css:site.css#--gap", CodeEdgeKinds.Uses, 11, CodeConfidences.Exact),
            ],
        },
        new("base.css", "body { margin: 0; }\n"),
        new("styles.scss", Scss)
        {
            Nodes =
            [
                new("css:styles.scss#@mixin rounded", CodeNodeKinds.Member, (8, 10)),
                new("css:styles.scss#.panel", CodeNodeKinds.Member, (14, 28)),
                new("css:styles.scss#.panel__title", CodeNodeKinds.Member, (18, 20)),
                new("css:styles.scss#.wide", CodeNodeKinds.Member, (25, 25)),
            ],
            Edges =
            [
                new("file:styles.scss", "file:theme/_colors.scss", CodeEdgeKinds.Imports, 2, CodeConfidences.Exact),
                new("file:styles.scss", "file:src/list/_index.scss", CodeEdgeKinds.Imports, 3, CodeConfidences.Exact),
                new("css:styles.scss#.panel", "css:styles.scss#@mixin rounded", CodeEdgeKinds.Uses, 15, CodeConfidences.Exact),
                new("css:styles.scss#.panel", "css:styles.scss#%raised", CodeEdgeKinds.Inherits, 16, CodeConfidences.Exact),
                new("css:styles.scss#.panel", "css:theme/_colors.scss#$primary", CodeEdgeKinds.Uses, 17, CodeConfidences.Exact),
            ],
        },
        new("theme/_colors.scss", "$primary: #00f;\n"),
        new("src/list/_index.scss", "@mixin list-reset {\n  margin: 0;\n}\n"),
        new("styles.sass", Sass)
        {
            Nodes =
            [
                new("css:styles.sass#.edge", CodeNodeKinds.Member, (6, 11)),
                new("css:styles.sass#.inner", CodeNodeKinds.Member, (9, 11)),
                new("css:styles.sass#.other", CodeNodeKinds.Member, (12, 13)),
            ],
            Edges = [new("css:styles.sass#.inner", "css:styles.sass#@mixin bevel", CodeEdgeKinds.Uses, 10, CodeConfidences.Exact)],
        },
        new("styles.less", Less)
        {
            Nodes = [new("css:styles.less#.wide", CodeNodeKinds.Member, (6, 10))],
            Edges = [new("css:styles.less#.wide", "css:styles.less#.rounded", CodeEdgeKinds.Uses, 8, CodeConfidences.Exact)],
        },
        new("src/Button.module.css", Module)
        {
            Edges = [new("css:src/Button.module.css#.btn-large", "css:src/Button.module.css#.btn-base", CodeEdgeKinds.Uses, 3, CodeConfidences.Exact)],
        },
    ];

    /// <summary>
    /// Each simple part of each selector is a rule over its own rule's lines - inside an <c>@media</c> block too, and
    /// past one - named as CSS reads it, escapes read; each custom property and <c>@property</c> a style variable; a
    /// <c>@keyframes</c> and a <c>@font-face</c> a member of their own kind; a comment above a rule its summary; a
    /// <c>var(--x)</c> a use of the variable made from the rule that holds it; an <c>@import</c> of a file an import of it; and no
    /// pseudo-class, <c>:not()</c> or combinator a part, though an <c>:is()</c>'s selectors are.
    /// </summary>
    [Fact]
    public void EachSelectorsPartsAreRules_OverTheirOwnLines_AndItsVariablesAndUsesRead()
    {
        var extract = Read("site.css", Sheet);

        Assert.Equal(
            [
                ("--gap", CodeSubKinds.StyleVariable, 5, 5), (".btn", CodeSubKinds.Rule, 8, 12), (".btn-primary", CodeSubKinds.Rule, 8, 12), ("#main", CodeSubKinds.Rule, 13, 16),
                ("my-widget", CodeSubKinds.Rule, 13, 16), ("slide-in", CodeSubKinds.Keyframes, 17, 20), (".card", CodeSubKinds.Rule, 22, 24), (".lead", CodeSubKinds.Rule, 26, 28),
                (".intro", CodeSubKinds.Rule, 26, 28), ("p", CodeSubKinds.Rule, 26, 28), (".sm:hidden", CodeSubKinds.Rule, 29, 31), ("inter", CodeSubKinds.FontFace, 32, 35),
                ("--angle", CodeSubKinds.StyleVariable, 36, 40), (".after", CodeSubKinds.Rule, 41, 41),
            ],
            extract.Nodes.Select(node => (node.Name, node.SubKind!, node.Start, node.End)));
        Assert.Equal(".btn, .btn-primary", extract.Nodes[1].Signature);
        Assert.Equal(["The primary buttons."], extract.Nodes.Select(node => node.Summary).OfType<string>().Distinct());
        Assert.Equal(
            [("css:site.css#.btn", CodeEdgeKinds.Uses, "--gap", 11, CodeQualifierKinds.StyleVariable), ("css:site.css#.after", CodeEdgeKinds.Uses, "--angle", 41, CodeQualifierKinds.StyleVariable)],
            extract.References.Select(reference => (reference.From.Whole(), reference.Kind, reference.Name, reference.Line, reference.Qualifier?.Kind)));
        Assert.Equal([("base.css", 2), ("print.css", 3)], extract.Imports.Select(import => (import.Target, import.Line)));
        Assert.Empty(extract.Dynamic);
    }

    /// <summary>
    /// A conditional group's block - <c>@media</c>, <c>@supports</c>, <c>@container</c>, <c>@layer</c>, an SCSS
    /// <c>@include</c>'s - is read as the block it holds, and the sheet goes on past it: a minified sheet's too, whose
    /// blocks stand on one line.
    /// </summary>
    [Fact]
    public void AConditionalGroupsBlock_IsReadAsTheBlockItHolds_AndTheSheetGoesOnPastIt()
    {
        var css = Read("a.css", """
            .a{}
            @media (min-width: 768px) { .b{} }
            .c{}
            @supports (display: grid) {
              .d { color: red; }
            }
            @container card (width > 30em) { .e {} }
            @layer base { .f {} }
            @media(max-width:600px){.g{color:red}}.h{}
            """);

        Assert.Equal([(".a", 1), (".b", 2), (".c", 3), (".d", 5), (".e", 7), (".f", 8), (".g", 9), (".h", 9)], css.Nodes.Select(node => (node.Name, node.Start)));

        var scss = Read("a.scss", ".x { @include mq(md) { .y { color: red; } } margin: 0; }\n.z {}\n");

        Assert.Equal([".x", ".y", ".z"], scss.Nodes.Select(node => node.Name));
    }

    /// <summary>
    /// A string ends only at its own quote not escaped, a <c>url(...)</c> is one token, and a <c>//</c> is a comment only
    /// where the sheet's syntax writes one, never inside either: no rule is lost to a quote, a URL or a comment, and a
    /// string left open throws nothing.
    /// </summary>
    [Fact]
    public void AStringEndsAtItsOwnQuote_AUrlIsOneToken_AndALineCommentIsOneOnlyWhereTheSyntaxWritesIt()
    {
        var css = Read("a.css", """
            @import url("https://fonts.example.com/css?family=Roboto");
            .a{}
            .b { background: url("//cdn/x.png"); }
            .c { content: "it's"; }
            .d { background: url(http://x.example.com/a.png); } .e { color: red; }
            .f { background: url("data:image/svg+xml,%3csvg xmlns='http://www.w3.org/2000/svg'"); }
            .g { content: "a \" b"; } .h {}
            // .i {}
            """);

        Assert.Equal([".a", ".b", ".c", ".d", ".e", ".f", ".g", ".h", ".i"], css.Nodes.Select(node => node.Name));
        Assert.Empty(css.Imports);

        var scss = Read("a.scss", ".a { b: url(//cdn/x.png); } .b {}\n// .i {}\n.j {}\n");

        Assert.Equal([(".a", 1, 1), (".b", 1, 1), (".j", 3, 3)], scss.Nodes.Select(node => (node.Name, node.Start, node.End)));
        Assert.Equal([".a"], Read("a.css", ".a { content: \"it's }\n").Nodes.Select(node => node.Name));
    }

    /// <summary>
    /// Blocks, a pseudo-class's selectors and interpolations nested to any depth are each read whole, and the sheet goes on
    /// past them - each read with a stack of what it is in, never a call into itself, so no nesting ends the process, and
    /// in time that grows with the sheet: a block's own variable is its own at every depth, the selector innermost in a
    /// run of <c>:is()</c> is a part of its rule, and what the innermost of a run of interpolations uses is read, nothing
    /// said dynamic.
    /// </summary>
    [Fact]
    public void BlocksSelectorsAndInterpolationsNestedToAnyDepth_AreReadWhole_AndTheSheetGoesOnPastThem()
    {
        const int Depth = 50_000;
        var nested = string.Concat(Enumerable.Range(0, Depth).Select(depth => $".n{depth} {{ $v{depth}: 1; a: $v0 $v{depth} $out;\n")) + new string('}', Depth) + "\n.after {}\n";
        var watch = Stopwatch.StartNew();
        var blocks = Read("a.scss", nested);
        var selectors = Read("a.css", string.Concat(Enumerable.Repeat(":is(", Depth)) + "a" + new string(')', Depth) + " {}\n.b {}\n");
        var interpolations = Read("a.scss", ".a { b: \"" + string.Concat(Enumerable.Repeat("#{\"", Depth)) + "#{$deep}" + string.Concat(Enumerable.Repeat("\"}", Depth)) + "\"; }\n.c {}\n");
        watch.Stop();

        Assert.Equal([.. Enumerable.Range(0, Depth).Select(depth => $".n{depth}"), ".after"], blocks.Nodes.Select(node => node.Name));
        Assert.Equal([.. Enumerable.Range(0, Depth).Select(depth => ($"css:a.scss#.n{depth}", "$out"))], blocks.References.Select(reference => (reference.From.Whole(), reference.Name)));
        Assert.Equal(["a", ".b"], selectors.Nodes.Select(node => node.Name));
        Assert.Equal([".a", ".c"], interpolations.Nodes.Select(node => node.Name));
        Assert.Equal([("css:a.scss#.a", "$deep", 1)], interpolations.References.Select(reference => (reference.From.Whole(), reference.Name, reference.Line)));
        Assert.Empty(blocks.Dynamic.Concat(selectors.Dynamic).Concat(interpolations.Dynamic));

        // The slowest runner of the matrix - a shared two-core macOS under the suite's own load - read these three
        // sheets in twenty seconds; a wrong read, one that walks its stack for every depth, is minutes past any bound here.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"reading nesting {Depth} deep took {watch.Elapsed}");
    }

    /// <summary>A selector written over several lines counts each line once: the rules after it keep their own lines.</summary>
    [Fact]
    public void ASelectorOverSeveralLines_CountsEachLineOnce()
    {
        var extract = Read("a.scss", ".a,\n.b\n{\n  color: red;\n}\n.c {\n}\n");

        Assert.Equal([(".a", 1, 5), (".b", 1, 5), (".c", 6, 7)], extract.Nodes.Select(node => (node.Name, node.Start, node.End)));
    }

    /// <summary>
    /// A nested rule is read inside the block that holds it, to its own close: a declaration after it is the parent's,
    /// no constant, and the parent's close ends the parent alone, its lines its own.
    /// </summary>
    [Fact]
    public void ANestedRule_IsReadInsideTheBlockThatHoldsIt_ItsLinesItsOwn()
    {
        var extract = Read("a.scss", """
            .card { .title { color: red; } margin: 0; }
            .other { x: y; }
            .box {
              .head {
                a: b;
              }
              .body {
                c: d;
              }
            }
            """);

        Assert.Equal([(".card", 1, 1), (".title", 1, 1), (".other", 2, 2), (".box", 3, 10), (".head", 4, 6), (".body", 7, 9)], extract.Nodes.Select(node => (node.Name, node.Start, node.End)));
    }

    /// <summary>
    /// A Less mixin's call is a use of the rule it names, and Tailwind's directives no selectors: an <c>@apply</c> uses
    /// the rule of each class it applies, a variant's prefix and an <c>!important</c>'s mark left out, and a
    /// <c>@tailwind</c> is a statement of its own.
    /// </summary>
    [Fact]
    public void ALessMixinsCall_AndTailwindsDirectives_AreNoSelectors()
    {
        var less = Read("a.less", ".a { .rounded(); #ns > .shadow; color: red; } .b {}\n");

        Assert.Equal([(".a", 1, 1), (".b", 1, 1)], less.Nodes.Select(node => (node.Name, node.Start, node.End)));
        Assert.Equal([("css:a.less#.a", ".rounded"), ("css:a.less#.a", ".shadow")], less.References.Select(reference => (reference.From.Whole(), reference.Name)));
        Assert.All(less.References, reference => Assert.Equal(new ExtractQualifier(CodeQualifierKinds.Selector), reference.Qualifier));

        var tailwind = Read("a.css", "@tailwind base;\n@tailwind components;\n.btn {\n  @apply px-4 md:hover:bg-blue !font-bold;\n}\n");

        Assert.Equal([(".btn", 3, 5)], tailwind.Nodes.Select(node => (node.Name, node.Start, node.End)));
        Assert.Equal([".px-4", ".bg-blue", ".font-bold"], tailwind.References.Select(reference => reference.Name));
        Assert.All(tailwind.References, reference => Assert.Equal(("css:a.css#.btn", CodeEdgeKinds.Uses, 4, CodeQualifierKinds.Selector), (reference.From.Whole(), reference.Kind, reference.Line, reference.Qualifier?.Kind)));
    }

    /// <summary>
    /// A selector an interpolation writes is no rule, said as dynamic, and what the interpolation uses is read - from the
    /// sheet's top node, which holds it; one in a value is read for what it uses, and no rule.
    /// </summary>
    [Fact]
    public void ASelectorAnInterpolationWrites_IsNoRule_SaidDynamic_AndWhatItUsesRead()
    {
        var extract = Read("a.scss", ".a {}\n.col-#{$i} { width: 1px; }\n.y { width: calc(100% - #{$gutter}); }\n");

        Assert.Equal([".a", ".y", "(sheet)"], extract.Nodes.Select(node => node.Name));
        Assert.Equal([("css:a.scss#(sheet)", "$i", 2), ("css:a.scss#.y", "$gutter", 3)], extract.References.Select(reference => (reference.From.Whole(), reference.Name, reference.Line)));
        Assert.Equal([new ExtractDynamic(2, "a selector an interpolation writes, whose rule is no node")], extract.Dynamic);
    }

    /// <summary>
    /// Every reference is made from the node that holds it: a rule's from the rule - its subject, a nested one's own - a
    /// mixin's from the mixin, a variable's value's from the variable; what no node holds - a top-level <c>@include</c> -
    /// from the sheet's own top node, never from a node declared elsewhere in the sheet.
    /// </summary>
    [Fact]
    public void EveryReference_IsMadeFromTheNodeThatHoldsIt()
    {
        var extract = Read("a.scss", """
            @include reset;
            :root { --brand: var(--base); }
            .a {}
            .btn { color: var(--brand); .icon { fill: $ink; } }
            .base {}
            .card .child { @extend .base; }
            $radius: $size;
            @mixin rounded { border-radius: $radius; }
            .panel { @include rounded; @extend .btn; &:hover { color: var(--hover); } }
            """);

        Assert.Equal(
            [
                ("css:a.scss#(sheet)", CodeEdgeKinds.Uses, "reset", 1), ("css:a.scss#--brand", CodeEdgeKinds.Uses, "--base", 2),
                ("css:a.scss#.btn", CodeEdgeKinds.Uses, "--brand", 4), ("css:a.scss#.icon", CodeEdgeKinds.Uses, "$ink", 4),
                ("css:a.scss#.child", CodeEdgeKinds.Inherits, ".base", 6), ("css:a.scss#$radius", CodeEdgeKinds.Uses, "$size", 7),
                ("css:a.scss#@mixin rounded", CodeEdgeKinds.Uses, "$radius", 8), ("css:a.scss#.panel", CodeEdgeKinds.Uses, "rounded", 9),
                ("css:a.scss#.panel", CodeEdgeKinds.Inherits, ".btn", 9), ("css:a.scss#.panel", CodeEdgeKinds.Uses, "--hover", 9),
            ],
            extract.References.Select(reference => (reference.From.Whole(), reference.Kind, reference.Name, reference.Line)));
    }

    /// <summary>An <c>@extend</c> links to the rule it names as an inheritance, through the selector it writes, in another sheet too.</summary>
    [Fact]
    public void AnExtend_LinksToTheRuleItNames_AsAnInheritance()
    {
        var linked = Link(("base.scss", ".base { color: red; }\n%raised { x: y; }\n"), ("child.scss", ".child {\n  @extend .base;\n  @extend %raised;\n}\n"));

        // Neither sheet imports the other: the repository's rule, found by its name alone.
        Assert.Equal(
            [
                new CodeEdge("css:child.scss#.child", "css:base.scss#.base", CodeEdgeKinds.Inherits, 2, CodeConfidences.Unique),
                new CodeEdge("css:child.scss#.child", "css:base.scss#%raised", CodeEdgeKinds.Inherits, 3, CodeConfidences.Unique),
            ],
            linked.Edges.Where(edge => edge.Kind == CodeEdgeKinds.Inherits));
    }

    /// <summary>
    /// An import is tried as the sheet's language finds it: Sass's partial puts its <c>_</c> before the file's name, not
    /// its directory, each of Sass's extensions is tried - the sheet's own first - then the directory's index, an
    /// <c>@import</c>'s import-only file first; a built-in module, a package, a URL and a host's path are no imports;
    /// Less adds <c>.less</c> to a name with none, CSS takes the name as written; a <c>@use</c> is reached by its namespace
    /// and a <c>@forward</c> is an export with its filters.
    /// </summary>
    [Fact]
    public void AnImport_IsTriedAsTheSheetsLanguageFindsIt()
    {
        var scss = Read("a.scss", """
            @use "abstracts/variables";
            @use "sass:math";
            @use "~pkg/x";
            @use "https://example.com/x";
            @use "/rooted/x";
            @import "mixins", "plain.css";
            @use "theme" as *;
            @forward "src/list" show list-reset, $gap;
            @import "_partial.scss";
            """);

        string[] tried(string target) =>
        [
            $"{Dir(target)}{Name(target)}.scss", $"{Dir(target)}_{Name(target)}.scss", $"{Dir(target)}{Name(target)}.sass", $"{Dir(target)}_{Name(target)}.sass",
            $"{Dir(target)}{Name(target)}.css", $"{Dir(target)}_{Name(target)}.css", $"{target}/index.scss", $"{target}/_index.scss", $"{target}/index.sass",
            $"{target}/_index.sass", $"{target}/index.css", $"{target}/_index.css",
        ];

        Assert.Equal(
            [
                .. tried("abstracts/variables").Select(target => (target, 1, "variables", false)),
                .. ((string[])["mixins.import.scss", "_mixins.import.scss", "mixins.import.sass", "_mixins.import.sass"]).Concat(tried("mixins")).Select(target => (target, 6, (string?)null, false)),
                ("plain.css", 6, null, false),
                .. tried("theme").Select(target => (target, 7, (string?)null, false)),
                .. tried("src/list").Select(target => (target, 8, (string?)null, true)),
                ("_partial.scss", 9, null, false),
            ],
            scss.Imports.Select(import => (import.Target, import.Line, import.Prefix, import.Export)));
        Assert.All(scss.Imports.Where(import => import.Line == 8), import => Assert.Equal(["list-reset", "$gap"], import.Show));

        // Each run of what an import may be is one import: its first stands for it, each after it in for the one before.
        Assert.Equal(
            ((int[])[12, 16, 1, 12, 12, 1]).SelectMany(run => Enumerable.Range(0, run).Select(at => at > 0)),
            scss.Imports.Select(import => import.Fallback));

        Assert.Equal([("mixins.less", 1), ("theme.css", 2)], Read("a.less", "@import (reference) \"mixins\";\n@import \"theme.css\";\n").Imports.Select(import => (import.Target, import.Line)));
        Assert.Equal([("theme", 1), ("print.css", 2)], Read("a.css", "@import \"theme\";\n@import url(print.css?v=2) print;\n").Imports.Select(import => (import.Target, import.Line)));

        var linked = Link(
            ("css/main.scss", "@use \"abstracts/variables\";\n@use \"components\";\n.a { color: variables.$ink; }\n"),
            ("css/abstracts/_variables.scss", "$ink: #000;\n"),
            ("css/components/_index.scss", ".card {}\n"));

        Assert.Equal(
            [
                new CodeEdge("file:css/main.scss", "file:css/abstracts/_variables.scss", CodeEdgeKinds.Imports, 1, CodeConfidences.Exact),
                new CodeEdge("file:css/main.scss", "file:css/components/_index.scss", CodeEdgeKinds.Imports, 2, CodeConfidences.Exact),
                new CodeEdge("css:css/main.scss#.a", "css:css/abstracts/_variables.scss#$ink", CodeEdgeKinds.Uses, 3, CodeConfidences.Exact),
            ],
            linked.Edges.Where(edge => edge.Kind is CodeEdgeKinds.Imports or CodeEdgeKinds.Uses).OrderBy(edge => edge.Kind, StringComparer.Ordinal).ThenBy(edge => edge.Line));
    }

    /// <summary>
    /// The indented syntax is read as SCSS is: a selector and the lines indented under it a rule over them, a list a
    /// comma ends going on to the next line, <c>=name</c> a mixin - its parameter its own, no use - and <c>+name</c> its
    /// use, a comment going on over the lines indented under it, and one that trails a line no part of its code.
    /// </summary>
    [Fact]
    public void TheIndentedSyntax_IsReadAsScssIs()
    {
        var extract = Read("styles.sass", Sass);

        Assert.Equal(
            [
                ("$gap", CodeSubKinds.StyleVariable, 3, 3), ("bevel", CodeSubKinds.StyleMixin, 4, 5), (".edge", CodeSubKinds.Rule, 6, 11), (".rim", CodeSubKinds.Rule, 6, 11),
                (".inner", CodeSubKinds.Rule, 9, 11), (".other", CodeSubKinds.Rule, 12, 13),
            ],
            extract.Nodes.Select(node => (node.Name, node.SubKind!, node.Start, node.End)));
        Assert.Equal("The sheet's own rules, in the indented syntax.", extract.Nodes[0].Summary);
        Assert.Equal(
            [("css:styles.sass#.edge", "$gap", 8, CodeQualifierKinds.StyleVariable), ("css:styles.sass#.inner", "bevel", 10, CodeQualifierKinds.StyleMixin)],
            extract.References.Select(reference => (reference.From.Whole(), reference.Name, reference.Line, reference.Qualifier?.Kind)));
    }

    /// <summary>
    /// The indented syntax's lines are read by the sheet's own lexer: an escaped bracket or quote in a selector - a
    /// Tailwind class - opens nothing, so the rules after it keep their blocks, and a string and an unquoted
    /// <c>url(...)</c> hold no comment and no bracket of the line's.
    /// </summary>
    [Fact]
    public void TheIndentedSyntaxsLines_AreReadByTheSheetsLexer_AnEscapedBracketOpeningNothing()
    {
        var extract = Read("a.sass", ".content-\\[\\'x\\'\\] // tailwind-style escaped class\n  color: red\n  .inner\n    margin: 0\n.after\n  background: url(//cdn/a.png) // a comment\n  content: \"( // [\"\n.last\n  padding: 0\n");

        Assert.Equal(
            [(".content-['x']", 1, 4), (".inner", 3, 4), (".after", 5, 7), (".last", 8, 9)],
            extract.Nodes.Select(node => (node.Name, node.Start, node.End)));
        Assert.Empty(extract.Dynamic);
    }

    /// <summary>
    /// A name an <c>animation</c> or an <c>animation-name</c> gives - a vendor's prefix left out, a string's among them - is
    /// a use of the keyframes of that name, through a qualifier that says so: the shorthand's own words, a time, a
    /// function and what it holds, and a variable are none, and the use reaches the sheet's keyframes and no rule or code of
    /// that name.
    /// </summary>
    [Fact]
    public void AnAnimationsName_IsAUseOfItsKeyframes()
    {
        var extract = Read("a.css", "@keyframes spin { to { a: b; } }\n.spinner {\n  animation: spin 1s cubic-bezier(0, 0, 1, 1) infinite alternate var(--more);\n  -webkit-animation-name: pulse, \"fade\";\n}\n");

        Assert.Equal(
            [("css:a.css#.spinner", "spin", 3, CodeQualifierKinds.Keyframes), ("css:a.css#.spinner", "pulse", 4, CodeQualifierKinds.Keyframes), ("css:a.css#.spinner", "fade", 4, CodeQualifierKinds.Keyframes)],
            extract.References.Where(reference => reference.Qualifier?.Kind != CodeQualifierKinds.StyleVariable).Select(reference => (reference.From.Whole(), reference.Name, reference.Line, reference.Qualifier?.Kind)));

        var linked = CodeGraphLinker.Link(
        [
            new CodeGraphFile("a.css", "css", extract),
            new CodeGraphFile("spin.css", "css", Read("spin.css", "spin { a: b; }\n")),
            new CodeGraphFile("spin.js", "javascript", new JavaScriptExtractor().Extract(new CodeFileInput("spin.js", "export const spin = 1;\n"))),
        ]);

        Assert.Equal(
            [new CodeEdge("css:a.css#.spinner", "css:a.css#@keyframes spin", CodeEdgeKinds.Uses, 3, CodeConfidences.Exact)],
            linked.Edges.Where(edge => edge.Kind == CodeEdgeKinds.Uses));
    }

    /// <summary>
    /// A sheet named as a CSS module exports its classes, ids and keyframes by their names - camel-cased too - so a
    /// script's <c>styles.btn</c> binds to the rule <c>.btn</c>; a sheet named otherwise exports nothing.
    /// </summary>
    [Fact]
    public void ASheetNamedAsACssModule_ExportsItsClasses_AndAScriptsMemberBindsToTheRule()
    {
        var module = Read("src/Button.module.css", ".btn { a: b; }\n.btn-primary { a: b; }\n#main {}\n@keyframes spin { from { a: b; } }\ndiv {}\n");

        Assert.Equal(
            [("btn", (string?)null), ("btn-primary", "btnPrimary"), ("main", null), ("spin", null)],
            module.Nodes.Where(node => node.Exports is not null).Select(node => (node.Exports![0], node.Exports.Count > 1 ? node.Exports[1] : null)));
        Assert.All(Read("src/Button.css", ".btn { a: b; }\n").Nodes, node => Assert.Null(node.Exports));

        var linked = CodeGraphLinker.Link(
        [
            new CodeGraphFile("src/Button.module.css", "css", module),
            new CodeGraphFile("src/app.js", "javascript", new JavaScriptExtractor().Extract(new CodeFileInput("src/app.js", "import styles from './Button.module.css';\nimport { btn, btnPrimary as primary, main } from './Button.module.css';\nexport function f() {\n  return styles.btn + styles.btnPrimary;\n}\nexport function g() {\n  return btn + primary + main;\n}\n"))),
        ]);

        // A named import binds as the default import's member does, by a name the module exports the node by.
        Assert.Equal(
            [
                new CodeEdge("javascript:src/app.js.f", "css:src/Button.module.css#.btn", CodeEdgeKinds.Uses, 4, CodeConfidences.Exact),
                new CodeEdge("javascript:src/app.js.f", "css:src/Button.module.css#.btn-primary", CodeEdgeKinds.Uses, 4, CodeConfidences.Exact),
                new CodeEdge("javascript:src/app.js.g", "css:src/Button.module.css#.btn", CodeEdgeKinds.Uses, 7, CodeConfidences.Unique),
                new CodeEdge("javascript:src/app.js.g", "css:src/Button.module.css#.btn-primary", CodeEdgeKinds.Uses, 7, CodeConfidences.Exact),
                new CodeEdge("javascript:src/app.js.g", "css:src/Button.module.css##main", CodeEdgeKinds.Uses, 7, CodeConfidences.Unique),
            ],
            linked.Edges.Where(edge => edge.Kind == CodeEdgeKinds.Uses));
    }

    /// <summary>
    /// A default import of a style sheet that is no CSS module binds nothing, as a bundler gives it nothing by a name: a
    /// script's <c>styles.button</c> reaches none of the sheet's nodes - its element rule <c>button</c>, its variable - and
    /// the import of the file is kept.
    /// </summary>
    [Fact]
    public void ADefaultImportOfASheetNoModule_BindsNothing()
    {
        var sheet = Read("src/Button.css", "button { a: b; }\n.button { a: b; }\n");
        var script = new JavaScriptExtractor().Extract(new CodeFileInput("src/app.js", "import styles from './Button.css';\nexport function f() {\n  return styles.button;\n}\n"));

        var linked = CodeGraphLinker.Link([new CodeGraphFile("src/Button.css", "css", sheet), new CodeGraphFile("src/app.js", "javascript", script)]);

        Assert.Equal(
            [new CodeEdge(CodeGraphIds.FileId("src/app.js"), CodeGraphIds.FileId("src/Button.css"), CodeEdgeKinds.Imports, 1, CodeConfidences.Exact)],
            linked.Edges.Where(edge => edge.Kind is CodeEdgeKinds.Uses or CodeEdgeKinds.Imports));
    }

    /// <summary>
    /// A nested rule's <c>&amp;</c> with a suffix, where it stands for one selector, is a part of its name with the suffix
    /// written on, as SCSS and Less write BEM's; where it stands for several, one node named <c>&amp;</c> and the suffix,
    /// written onto each of them (<see cref="ExtractNode.SuffixOf"/>) - and a name it writes, used from its own sheet or
    /// another, links to it, one it does not write to nothing.
    /// </summary>
    [Fact]
    public void AnAmpersandsSuffix_IsAPartOfItsParentsNameWithTheSuffix_OrOneNodeWrittenOntoEachOfSeveral()
    {
        var extract = Read("a.scss", ".card, .tile {\n  &__title { &--big { a: b; } }\n  &:hover { a: b; }\n  &.lifted { a: b; }\n}\n.box { &__head { &--open { a: b; } } }\n.top { .q { @extend .tile__title--big; } }\n");

        Assert.Equal(
            [
                ("css:a.scss#.card", ".card", null), ("css:a.scss#.tile", ".tile", null), ("css:a.scss#.card&__title", "&__title", "css:a.scss#.card css:a.scss#.tile"),
                ("css:a.scss#.card&__title&--big", "&--big", "css:a.scss#.card&__title"), ("css:a.scss#.lifted", ".lifted", null), ("css:a.scss#.box", ".box", null),
                ("css:a.scss#.box__head", ".box__head", null), ("css:a.scss#.box__head--open", ".box__head--open", null), ("css:a.scss#.top", ".top", null), ("css:a.scss#.q", ".q", (string?)null),
            ],
            extract.Nodes.Select(node => (node.Id.Whole(), node.Name, node.SuffixOf is { } onto ? string.Join(" ", onto) : null)));

        var linked = Linked(("a.scss", extract), ("b.scss", Read("b.scss", ".z { @extend .card__title; @extend .tile__title--small; @extend .card__title-big; }\n")));

        Assert.Equal(
            [
                new CodeEdge("css:a.scss#.q", "css:a.scss#.card&__title&--big", CodeEdgeKinds.Inherits, 7, CodeConfidences.Exact),
                new CodeEdge("css:b.scss#.z", "css:a.scss#.card&__title", CodeEdgeKinds.Inherits, 1, CodeConfidences.Unique),
            ],
            linked.Edges.Where(edge => edge.Kind == CodeEdgeKinds.Inherits));
    }

    /// <summary>
    /// Lists nested in lists are as many nodes as the sheet writes, never the product of every list: twenty levels of a
    /// two-way list - a million names - are forty-two nodes, read and linked at once, each a suffix written onto the
    /// level's above; a name they write links to the deepest that writes it, nothing is said dynamic, and a list that
    /// writes one selector twice is one.
    /// </summary>
    [Fact]
    public void ListsNestedInLists_AreAsManyNodesAsTheSheetWrites_EachNameTheyWriteLinked()
    {
        const int Levels = 20;
        var sheet = ".a, .b {" + string.Concat(Enumerable.Range(0, Levels).Select(level => $" &-x{level}, &-y{level} {{")) + " c: d;" + new string('}', Levels + 1) + "\n";
        var deepest = ".b" + string.Concat(Enumerable.Range(0, Levels).Select(level => level % 2 == 0 ? $"-x{level}" : $"-y{level}"));
        var watch = Stopwatch.StartNew();
        var extract = Read("a.scss", sheet);
        var linked = Linked(("a.scss", extract), ("b.scss", Read("b.scss", $".z {{ @extend {deepest}; @extend {deepest}-x; @extend .c-x0; }}\n")));
        watch.Stop();

        Assert.Equal(2 + (2 * Levels), extract.Nodes.Count);
        Assert.Equal(["css:a.scss#.a&-x0&-x1", "css:a.scss#.a&-x0&-y1"], extract.Nodes.Where(node => node.Name is "&-x1" or "&-y1").Select(node => node.Id.Whole()));
        Assert.All(extract.Nodes.Skip(4), node => Assert.Equal(2, node.SuffixOf!.Count));
        Assert.Empty(extract.Dynamic);
        Assert.Equal(
            [new CodeEdge("css:b.scss#.z", Assert.Single(extract.Nodes, node => node.Name == $"&-y{Levels - 1}").Id.Whole(), CodeEdgeKinds.Inherits, 1, CodeConfidences.Unique)],
            linked.Edges.Where(edge => edge.Kind == CodeEdgeKinds.Inherits));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"reading and linking {Levels} levels took {watch.Elapsed}");

        Assert.Equal([".a", ".a-x"], Read("a.scss", ".a, .a { &-x, &-x { c: d; } }\n").Nodes.Select(node => node.Name));
    }

    /// <summary>
    /// In a CSS module a suffix written onto several classes is exported as each name it writes is - as written and
    /// camel-cased, a class that ends in a dash upper-casing the suffix's first letter - so a script's member, and a named
    /// import, of a name it writes binds to its node, and one it does not write to nothing.
    /// </summary>
    [Fact]
    public void AModulesSuffixWrittenOntoSeveral_IsExportedByEachNameItWrites()
    {
        var module = Read("src/Button.module.scss", ".a, .b { &-x { c: d; } }\n.btn-, .card- { &big { c: d; } }\n");

        Assert.Equal(
            [("&-x", "&-x &X"), ("&big", "&big &Big")],
            module.Nodes.Where(node => node.SuffixOf is not null).Select(node => (node.Name, string.Join(" ", node.Exports!))));

        var script = new JavaScriptExtractor().Extract(new CodeFileInput("src/app.js", """
            import styles from './Button.module.scss';
            import { aX, bX as q } from './Button.module.scss';
            export function f() {
              const a = styles.aX;
              const b = styles.bX;
              const c = styles.btnBig;
              const d = styles.cardBig;
              const e = styles.btnbig + styles.aXx + styles.ax;
              const g = aX;
              return q;
            }
            """));
        var linked = CodeGraphLinker.Link([new CodeGraphFile("src/Button.module.scss", "css", module), new CodeGraphFile("src/app.js", "javascript", script)]);

        Assert.Equal(
            [
                new CodeEdge("javascript:src/app.js.f", "css:src/Button.module.scss#.a&-x", CodeEdgeKinds.Uses, 4, CodeConfidences.Exact),
                new CodeEdge("javascript:src/app.js.f", "css:src/Button.module.scss#.a&-x", CodeEdgeKinds.Uses, 5, CodeConfidences.Exact),
                new CodeEdge("javascript:src/app.js.f", "css:src/Button.module.scss#.btn-&big", CodeEdgeKinds.Uses, 6, CodeConfidences.Exact),
                new CodeEdge("javascript:src/app.js.f", "css:src/Button.module.scss#.btn-&big", CodeEdgeKinds.Uses, 7, CodeConfidences.Exact),
                new CodeEdge("javascript:src/app.js.f", "css:src/Button.module.scss#.a&-x", CodeEdgeKinds.Uses, 9, CodeConfidences.Unique),
                new CodeEdge("javascript:src/app.js.f", "css:src/Button.module.scss#.a&-x", CodeEdgeKinds.Uses, 10, CodeConfidences.Exact),
            ],
            linked.Edges.Where(edge => edge.Kind == CodeEdgeKinds.Uses).OrderBy(edge => edge.Line));
    }

    /// <summary>
    /// A style block another implementation reads is read in the syntax its <c>lang</c> names, else its <c>type</c> as
    /// HTML writes one, as CSS where it names neither, and not at all where it names a syntax no style sheet's reader reads.
    /// </summary>
    [Fact]
    public void AStyleBlock_IsReadInTheSyntaxItsLangOrTypeNames()
    {
        const string Block = ".a { b: c; }\n// .b { }\n";

        string? Ids(string? lang, string? type) => CssExtractor.Read("vue", new CodeFileInput("x.vue", Block), lang, type) is { } read ? string.Join(" ", read.Nodes.Select(node => node.Id)) : null;

        Assert.Equal("vue:x.vue#.a", Ids("scss", null));
        Assert.Equal("vue:x.vue#.a", Ids("less", "text/css"));
        Assert.Equal("vue:x.vue#.a", Ids(null, "text/x-scss"));
        Assert.Equal("vue:x.vue#.a", Ids(null, "text/less"));
        Assert.Equal("vue:x.vue#.a vue:x.vue#.b", Ids(null, null));
        Assert.Equal("vue:x.vue#.a vue:x.vue#.b", Ids(null, "text/css"));
        Assert.Equal("vue:x.vue#.a vue:x.vue#.b", Ids("postcss", null));
        Assert.Null(Ids("stylus", null));
        Assert.Null(Ids(null, "text/x-template"));
    }

    /// <summary>
    /// An escape in a name is the character it stands for, as CSS reads one: an escaped space or tab is part of the name,
    /// a hex escape takes the one white space after it, and the sheet goes on past each.
    /// </summary>
    [Fact]
    public void AnEscapeInAName_IsTheCharacterItStandsFor_AnEscapedSpaceOrTabAmongThem()
    {
        var extract = Read("a.css", ".a\\ b { color: red; }\n.c\\\tx {}\n.d\\31 0 {}\n.e {}\n");

        Assert.Equal([".a b", ".c\tx", ".d10", ".e"], extract.Nodes.Select(node => node.Name));
    }

    /// <summary>A backslash before a line break in a string goes on past the break whole, so a sheet's rules are the same under LF and CR LF.</summary>
    [Fact]
    public void AnEscapedLineBreakInAString_GoesOnAlikeUnderLfAndCrLf()
    {
        const string Text = ".c { content: \"a\\\nb\"; }\n.d { x: y; }\n";

        Assert.Equal([(".c", 1, 2), (".d", 3, 3)], Read("a.css", Text).Nodes.Select(node => (node.Name, node.Start, node.End)));
        Assert.Equal([(".c", 1, 2), (".d", 3, 3)], Read("a.css", Text.Replace("\n", "\r\n", StringComparison.Ordinal)).Nodes.Select(node => (node.Name, node.Start, node.End)));
    }

    /// <summary>
    /// A string's value is decoded by the sheet's escapes, the one rule its names are read by - an import's path among
    /// them - and each string and unquoted <c>url(...)</c> a value writes is kept as a literal of the node that holds it,
    /// a <c>url(...)</c>'s without the white space about its address.
    /// </summary>
    [Fact]
    public void AStringsValue_IsDecodedByTheSheetsEscapes_AndKeptAsALiteralOfTheNodeThatHoldsIt()
    {
        var extract = Read("a.css", "@import \"theme\\2d dark.css\";\n@import 'it\\'s.css';\n.a { content: \"hello\\A world\"; background: url( img/a.png ); }\n");

        Assert.Equal(["theme-dark.css", "it's.css"], extract.Imports.Select(import => import.Target));
        Assert.Equal([("hello\nworld", 3, "css:a.css#.a"), ("img/a.png", 3, "css:a.css#.a")], LiteralRow.Of(extract).Select(literal => (literal.Value, literal.Line, literal.Node)));
    }

    /// <summary>
    /// Two declarations of one name and two kinds are two nodes, each its kind's: a mixin named as keyframes are, and an
    /// element rule named as a mixin - each linked to by the uses of its own kind alone.
    /// </summary>
    [Fact]
    public void TwoDeclarationsOfOneNameAndTwoKinds_AreTwoNodes_EachReachedByItsOwnKind()
    {
        var extract = Read("a.scss", "@keyframes fade-in { from { a: b; } }\n@mixin fade-in($d: 1s) { animation: fade-in $d; }\n.modal { @include fade-in(2s); }\n@mixin button { a: b; }\nbutton { @include button; }\n");
        var linked = Linked(("a.scss", extract));

        Assert.Equal(
            [("css:a.scss#@keyframes fade-in", CodeSubKinds.Keyframes), ("css:a.scss#@mixin fade-in", CodeSubKinds.StyleMixin), ("css:a.scss#.modal", CodeSubKinds.Rule), ("css:a.scss#@mixin button", CodeSubKinds.StyleMixin), ("css:a.scss#button", CodeSubKinds.Rule)],
            extract.Nodes.Select(node => (node.Id.Whole(), node.SubKind!)));
        Assert.Equal(
            [
                new CodeEdge("css:a.scss#@mixin fade-in", "css:a.scss#@keyframes fade-in", CodeEdgeKinds.Uses, 2, CodeConfidences.Exact),
                new CodeEdge("css:a.scss#.modal", "css:a.scss#@mixin fade-in", CodeEdgeKinds.Uses, 3, CodeConfidences.Exact),
                new CodeEdge("css:a.scss#button", "css:a.scss#@mixin button", CodeEdgeKinds.Uses, 5, CodeConfidences.Exact),
            ],
            linked.Edges.Where(edge => edge.Kind == CodeEdgeKinds.Uses));
    }

    /// <summary>
    /// What no node holds - a top-level <c>@include</c> and its block's head, a flow control's head, a rule of no parts -
    /// is made from the sheet's own top node over the lines its text stands on, and so are the literals written there:
    /// never from a node declared elsewhere in the sheet.
    /// </summary>
    [Fact]
    public void WhatNoNodeHolds_IsMadeFromTheSheetsTopNode()
    {
        var extract = Read("a.scss", "// The sheet.\n$gap: 4px;\n@include font-face(\"Roboto Slab\");\n@include mq(tablet) { .nav { a: b; } }\n@if $theme == dark { .x { a: b; } }\n* { color: var(--ink); }\n:root { --ink: #000; }\n");

        Assert.Equal(("(sheet)", CodeSubKinds.Sheet, 1, 7), extract.Nodes.Where(node => node.Name == "(sheet)").Select(node => (node.Name, node.SubKind!, node.Start, node.End)).Single());
        Assert.Equal(
            [("font-face", 3), ("mq", 4), ("$theme", 5), ("--ink", 6)],
            extract.References.Where(reference => reference.From == "css:a.scss#(sheet)").Select(reference => (reference.Name, reference.Line)));
        Assert.Equal([("Roboto Slab", "css:a.scss#(sheet)")], LiteralRow.Of(extract).Select(literal => (literal.Value, literal.Node)));
        Assert.DoesNotContain(Read("a.scss", ".a { b: c; }\n").Nodes, node => node.Name == "(sheet)");
    }

    /// <summary>
    /// A selector is found nearest first: the sheet's own rule, exact, before any other file's; what the sheet imports,
    /// before the repository's; a CSS module's <c>composes ... from</c> an import of that file, its classes the ones that
    /// file exports - not another file's of the name; and a module's own class and a scoped style block's rule are seen
    /// from their own file alone, never by another file's selector.
    /// </summary>
    [Fact]
    public void ASelector_IsFoundNearestFirst_AndAModulesOrAScopedBlocksRuleFromItsOwnFileAlone()
    {
        var own = Link(("a.scss", ".btn {}\n.x { @extend .btn; }\n"), ("b.scss", ".btn {}\n"));

        Assert.Equal([new CodeEdge("css:a.scss#.x", "css:a.scss#.btn", CodeEdgeKinds.Inherits, 2, CodeConfidences.Exact)], own.Edges.Where(edge => edge.Kind == CodeEdgeKinds.Inherits));

        var imported = Link(("c.less", "@import \"mixins\";\n.y { .rounded(); }\n"), ("mixins.less", ".rounded() { a: b; }\n"), ("vendor.css", ".rounded { a: b; }\n"));

        Assert.Equal([new CodeEdge("css:c.less#.y", "css:mixins.less#.rounded", CodeEdgeKinds.Uses, 2, CodeConfidences.Unique)], imported.Edges.Where(edge => edge.Kind == CodeEdgeKinds.Uses));

        var composed = Link(("src/Button.module.css", ".btn { composes: base from \"./base.module.css\"; composes: own; }\n.own {}\n"), ("src/base.module.css", ".base {}\n"), ("src/other.css", ".base {}\n.own {}\n"));

        Assert.Equal(
            [
                new CodeEdge("css:src/Button.module.css#.btn", "css:src/base.module.css#.base", CodeEdgeKinds.Uses, 1, CodeConfidences.Exact),
                new CodeEdge("css:src/Button.module.css#.btn", "css:src/Button.module.css#.own", CodeEdgeKinds.Uses, 1, CodeConfidences.Exact),
                new CodeEdge("file:src/Button.module.css", "file:src/base.module.css", CodeEdgeKinds.Imports, 1, CodeConfidences.Exact),
            ],
            composed.Edges.Where(edge => edge.Kind is CodeEdgeKinds.Uses or CodeEdgeKinds.Imports));

        // A page's class names no module's class and no other component's scoped rule; a component's own class reaches its own.
        var page = new HtmlExtractor().Extract(new CodeFileInput("page.html", "<div class=\"btn box\"></div>\n"));
        var module = Read("src/x.module.css", ".btn {}\n");
        FileExtract Component(string path) => CssExtractor.Read("vue", new CodeFileInput(path, ".box { a: b; }\n.card .box {}\n"), lang: null, type: null, StyleScope.Scoped)! is var block
            ? block with { References = [new ExtractReference(block.Nodes[0].Id, CodeEdgeKinds.Uses, ".box", 1) { Qualifier = new ExtractQualifier(CodeQualifierKinds.Selector) }] }
            : null!;

        var scoped = CodeGraphLinker.Link(
        [
            new CodeGraphFile("page.html", "html", page),
            new CodeGraphFile("src/x.module.css", "css", module),
            new CodeGraphFile("Card.vue", "vue", Component("Card.vue")),
            new CodeGraphFile("Other.vue", "vue", Component("Other.vue")),
        ]);

        Assert.Equal(
            [("vue:Card.vue#.box", "vue:Card.vue#.box"), ("vue:Other.vue#.box", "vue:Other.vue#.box")],
            scoped.Edges.Where(edge => edge.Kind == CodeEdgeKinds.Uses).Select(edge => (edge.From, edge.To)));
        Assert.Equal(
            [(".box", "file:Card.vue"), (".card", null), (".box", "file:Other.vue"), (".card", null)],
            ((string[])["Card.vue", "Other.vue"]).SelectMany(path => Component(path).Nodes).Select(node => (node.Name, node.SeenIn)));
    }

    /// <summary>
    /// A name a block declares of its own is no use of the sheet's: a mixin's and a function's parameters, a loop's
    /// variables, a variable a rule's block declares - but one it declares <c>!global</c> - a call's keyword names and a
    /// Less mixin's parameters; what the defaults and the arguments use is read.
    /// </summary>
    [Fact]
    public void ANameABlockDeclaresOfItsOwn_IsNoUseOfTheSheets()
    {
        var scss = Read("a.scss", """
            @mixin rounded($size, $pad: $size + $base) { border-radius: $size; padding: $pad; }
            @each $color, $shade in $palette { .t { color: $color $shade; } }
            @for $i from 1 through $count { .c { order: $i; } }
            .x { $w: 1px; width: $w; $g: 2px !global; @include mq($from: $tablet); }
            .y { height: $g; width: $w $i $color; }
            """);

        // What a block declares is its own to its close: the rule after it uses the sheet's of those names.
        Assert.Equal([("$base", 1), ("$palette", 2), ("$count", 3), ("mq", 4), ("$tablet", 4), ("$g", 5), ("$w", 5), ("$i", 5), ("$color", 5)], scss.References.Select(reference => (reference.Name, reference.Line)));
        Assert.Equal(["@mixin rounded", ".t", ".c", ".x", "$g", ".y", "(sheet)"], scss.Nodes.Select(node => { var id = node.Id.Whole(); return id[(id.IndexOf('#') + 1)..]; }));

        var less = Read("a.less", ".rounded(@r: @base) { border-radius: @r; }\n.z { .rounded(@r: 3px); }\n");

        Assert.Equal([("@base", 1), (".rounded", 2)], less.References.Select(reference => (reference.Name, reference.Line)));
    }

    /// <summary>
    /// In a CSS module a <c>:global(...)</c>'s selectors, and those after a <c>:global</c> written alone, are the page's -
    /// nodes no file keeps and the module exports none of - and a <c>:local(...)</c>'s the module's own, exported.
    /// </summary>
    [Fact]
    public void AModulesGlobalSelectors_AreThePages_AndItsLocalOnesItsOwn()
    {
        var module = Read("src/a.module.css", ":global(.legacy) {}\n:local(.loc) {}\n:global .wide {}\n.own :global(.theirs) {}\n");

        Assert.Equal(
            [(".legacy", null, null), (".loc", "loc", "file:src/a.module.css"), (".wide", null, null), (".own", "own", "file:src/a.module.css"), (".theirs", null, (string?)null)],
            module.Nodes.Select(node => (node.Name, node.Exports is { } exports ? string.Join(" ", exports) : null, node.SeenIn?.Whole())));
    }

    /// <summary>
    /// An import's path and a keyframes' name an interpolation writes are no import and no node, said as dynamic, and
    /// what the interpolation uses is read from the node that holds it.
    /// </summary>
    [Fact]
    public void AnImportsPathOrAKeyframesNameAnInterpolationWrites_IsSaidDynamic_AndWhatItUsesRead()
    {
        var less = Read("a.less", "@import \"@{themes}/tidal-wave.less\";\n");
        var scss = Read("a.scss", "@import url(\"#{$t}/a.css\");\n@keyframes \"#{$n}\" { to { a: b; } }\n@keyframes #{$m}-x { to { a: b; } }\n");

        Assert.Empty(less.Imports.Concat(scss.Imports));
        Assert.Equal([("@themes", 1)], less.References.Select(reference => (reference.Name, reference.Line)));
        Assert.Equal([new ExtractDynamic(1, "an import whose path an interpolation writes, which is no import")], less.Dynamic);
        Assert.Equal([("$t", 1), ("$n", 2), ("$m", 3)], scss.References.Select(reference => (reference.Name, reference.Line)));
        Assert.Equal(
            [(1, "an import whose path an interpolation writes, which is no import"), (2, "a name an interpolation writes, whose node is not read"), (3, "a name an interpolation writes, whose node is not read")],
            scss.Dynamic.Select(dynamic => (dynamic.Line, dynamic.Why)));
        Assert.Equal(["(sheet)"], scss.Nodes.Select(node => node.Name));
    }

    /// <summary>An SCSS <c>@import</c> Sass compiles to CSS's own - of a <c>url(...)</c>, or one a media query follows - is tried as written, no Sass file.</summary>
    [Fact]
    public void AnScssImportSassLeavesToCss_IsTriedAsWritten()
    {
        Assert.Equal(["theme", "print.css", "x"], Read("a.scss", "@import url(theme);\n@import url(\"print.css\");\n@import \"x\" print;\n").Imports.Select(import => import.Target));
    }

    /// <summary>
    /// A call of a function a sheet declares is a use of it - through a module's namespace too - and a family a
    /// <c>font-family</c> or a <c>font</c> names a use of the font face of that family, matched without case as CSS
    /// matches one; CSS's own functions, a generic family and a variable are none.
    /// </summary>
    [Fact]
    public void AFunctionsCall_AndAFontsFamily_AreUsesOfWhatTheSheetDeclares()
    {
        var extract = Read("a.scss", """
            @use "sizes" as s;
            @function rem($px) { @return $px; }
            @font-face { font-family: "Open Sans"; src: url(a.woff2); }
            .a { width: rem(24px) calc(1px + 2px) var(--x) s.gap(1); }
            .b { font-family: "open sans", Arial, sans-serif, $stack; }
            .c { font: italic 12px/1.5 Open  Sans, serif; }
            """);

        Assert.Equal(
            [("rem", 4, CodeQualifierKinds.StyleFunction), ("--x", 4, CodeQualifierKinds.StyleVariable), ("gap", 4, CodeQualifierKinds.Prefix), ("$stack", 5, CodeQualifierKinds.StyleVariable), ("open sans", 5, CodeQualifierKinds.FontFace), ("arial", 5, CodeQualifierKinds.FontFace), ("open sans", 6, CodeQualifierKinds.FontFace)],
            extract.References.Select(reference => (reference.Name, reference.Line, reference.Qualifier?.Kind)));

        var linked = Linked(("a.scss", extract));

        Assert.Equal(
            [
                new CodeEdge("css:a.scss#.a", "css:a.scss#@function rem", CodeEdgeKinds.Uses, 4, CodeConfidences.Exact),
                new CodeEdge("css:a.scss#.b", "css:a.scss#@font-face open sans", CodeEdgeKinds.Uses, 5, CodeConfidences.Exact),
                new CodeEdge("css:a.scss#.c", "css:a.scss#@font-face open sans", CodeEdgeKinds.Uses, 6, CodeConfidences.Exact),
            ],
            linked.Edges.Where(edge => edge.Kind == CodeEdgeKinds.Uses));
    }

    /// <summary>A sheet of many rules is read in time that grows with its rules alone: one node a name, found by its name.</summary>
    [Fact]
    public void ASheetOfManyRules_IsReadInTimeThatGrowsWithItsRules()
    {
        var sheet = string.Concat(Enumerable.Range(0, 40_000).Select(at => $".r{at} {{ color: red; }}\n"));
        var watch = Stopwatch.StartNew();
        var extract = Read("big.css", sheet);
        watch.Stop();

        Assert.Equal(40_000, extract.Nodes.Count);

        // As above: the bound holds on the matrix's slowest runner, and a wrong read - one that walks its rules for
        // each rule - is far past it.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"reading 40,000 rules took {watch.Elapsed}");
    }

    private static string Dir(string target) => target[..(target.LastIndexOf('/') + 1)];

    private static string Name(string target) => target[(target.LastIndexOf('/') + 1)..];

    private static FileExtract Read(string path, string text) => new CssExtractor().Extract(new CodeFileInput(path, text));

    private static CodeGraphLinked Link(params (string Path, string Text)[] files)
        => Linked([.. files.Select(file => (file.Path, Read(file.Path, file.Text)))]);

    private static CodeGraphLinked Linked(params (string Path, FileExtract Extract)[] files)
        => CodeGraphLinker.Link([.. files.Select(file => new CodeGraphFile(file.Path, "css", file.Extract))]);
}
