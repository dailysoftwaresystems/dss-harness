namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// A style sheet - <c>.css</c>, <c>.scss</c>, <c>.sass</c> in its indented syntax and <c>.less</c> - read over the
/// structural scanner (<see cref="CssLexer"/>, <see cref="CssReading"/>) as the rules it holds: each simple part of each
/// selector - <c>.btn</c>, <c>#main</c>, <c>my-widget</c>, a placeholder's <c>%base</c> - a <c>member</c> SubKind
/// <c>rule</c> named as CSS reads it, over its rule's lines, a nested rule's and one inside an at-rule's block among
/// them, and a suffix a <c>&amp;</c> writes onto several selectors one node written onto each of them
/// (<see cref="ExtractNode.SuffixOf"/>); each custom property, <c>@property</c> and SCSS or Less variable a
/// <c>style-variable</c> - but a block's own; each <c>@mixin</c>, <c>@function</c>, <c>@keyframes</c> and
/// <c>@font-face</c> a member of its own kind (<see cref="CodeSubKinds"/>), which code reaches by no name alone. A
/// <c>var(--x)</c>, a <c>$x</c> and a Less <c>@x</c> are uses of the variable, an <c>@include</c> a use of the mixin, a
/// call a use of the function, a Less mixin call one of the rule, an animation's name a use of its keyframes and a
/// font's family one of its font face, each through a qualifier saying what it names; an <c>@extend</c> and a Less
/// <c>:extend</c> an inheritance of the rule it names, a Tailwind <c>@apply</c> and a CSS module's <c>composes</c> uses
/// of the rules of its classes, each made from the node that holds it - what none holds from the sheet's top node - and
/// looked for nearest first; an <c>@import</c>, <c>@use</c> and <c>@forward</c> naming a file of the tree is an import
/// of it, as the sheet's language finds it. A sheet named as a CSS module (<c>x.module.css</c>) keeps its classes, ids
/// and keyframes to itself and exports them by their names, so a script's <c>styles.btn</c> binds to <c>.btn</c>.
/// </summary>
public sealed class CssExtractor : ICodeGraphExtractor
{
    /// <inheritdoc/>
    public string Language => "css";

    /// <inheritdoc/>
    public int Version => 1;

    /// <inheritdoc/>
    public CodeGraphClaims Claims { get; } = CodeGraphClaims.OfExtensions(".css", ".scss", ".sass", ".less");

    /// <inheritdoc/>
    public bool Outlines => true;

    /// <inheritdoc/>
    public CodeGraphExtractorHelp Help { get; } = new(
        "Style sheets (.css, .scss, .sass in its indented syntax, .less): each simple part of each selector - a nested rule's, one inside an @media, @supports, @layer or any at-rule's block, and one an & writes a suffix to among them - as a rule node named as CSS reads it, escapes read, over its own rule's lines, a suffix an & writes onto several selectors (.a, .b { &-x {} }) one node named &-x written onto each, which every name it writes links to, so lists nested in lists are as many nodes as the sheet writes; each custom property, @property and SCSS or Less variable as a style-variable, but a variable a block declares without !global, a mixin's or a function's parameter and a loop's variable, the block's own and no use; each @mixin, @function, @keyframes and @font-face as a member of its own kind, which no name code writes reaches; a var(--x), a $x and a Less @x as uses of the variable, an @include as a use of the mixin, a call of a function a sheet may declare (rem(2px), m.rem(2px)) as a use of it, a Less mixin call as one of the rule, a name an animation or an animation-name gives (not one of the shorthand's own words) as a use of the keyframes, a family a font-family or a font names as a use of the font face, matched without case, an @extend and a Less :extend as an inheritance of the rule it names, and a Tailwind @apply and a CSS module's composes as uses of the rules of its classes, each made from the rule, mixin, function or variable that holds it - what none holds from the sheet's own (sheet) node - each string and url(...) a literal of it, and each looked for nearest first: the sheet's own, what it imports, then the repository's; an @import, @use and @forward naming a file of the tree as an import of it, tried as the sheet's language tries it - Sass's partials, index files and import-only files among them, an @import Sass leaves to CSS (url(...), or one a media query follows) as written - a @use by its namespace, a @forward with its show and hide, and a composes ... from an import of its file; a sheet named x.module.css (or .scss, .sass, .less) as a CSS module, whose classes, ids and keyframes - but those under :global - are its own, which no other file's selector reaches and a script's import binds to by their names, camel-cased too; and blocks, selectors and interpolations nested to any depth, read whole",
        [
            "a pseudo-class's, a pseudo-element's and an attribute's condition, and a :not() or a :has(): no parts of a rule of their own, but the selectors of an :is(), a :where(), a :global(), a :local(), a :deep() and a :slotted(), which are",
            "a rule's declarations, the custom properties and variables among them excepted, and a keyframe's from, to and percentages: no nodes of their own",
            "a selector, a name or a path an interpolation writes (#{...}, Less's @{...}): no node and no import, said as dynamic, what the interpolation uses read",
            "an @import, @use or @forward that names a package, a URL, a host's path or one of Sass's own modules (sass:math): no import of it; a @forward's prefix (as name-*): its names read as they are declared",
            "a & in what an @extend or a Less :extend() names, which Sass refuses, and in a Less mixin's call: it stands for nothing there",
            "a call of one of CSS's own functions (calc(), var(), url()), a call in a Less value - Less declares no function - and a generic font family (serif, system-ui): no reference",
        ]);

    /// <inheritdoc/>
    public FileExtract Extract(CodeFileInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return new CssReading(Language, input.Path, input.Text, SyntaxOf(input.Path), IsModule(input.Path) ? StyleScope.Module : StyleScope.Global).Read();
    }

    /// <summary>
    /// Reads a style block of markup - a Vue component's style, an HTML page's style element - as another implementation's
    /// extract, every id prefixed by <paramref name="language"/>, in the syntax the block says it is written in
    /// (<see cref="BlockSyntax"/>), its names whose <paramref name="scope"/> says: a component's <c>&lt;style scoped&gt;</c>
    /// is <see cref="StyleScope.Scoped"/>, its <c>&lt;style module&gt;</c> <see cref="StyleScope.Module"/>, and every
    /// other block <see cref="StyleScope.Global"/>. <see langword="null"/> where the block names a syntax no style sheet's
    /// reader reads.
    /// </summary>
    /// <param name="language">The implementation the extract is part of: the prefix of every id.</param>
    /// <param name="input">The block's text, at the lines it stands on in its own file.</param>
    /// <param name="lang">The block's <c>lang</c>, as a component writes it; <see langword="null"/> where it has none.</param>
    /// <param name="type">The block's <c>type</c>, as HTML writes it; <see langword="null"/> where it has none.</param>
    /// <param name="scope">Whose the block's names are.</param>
    internal static FileExtract? Read(string language, CodeFileInput input, string? lang, string? type, StyleScope scope = StyleScope.Global)
    {
        ArgumentNullException.ThrowIfNull(input);
        return BlockSyntax(lang, type) is { } syntax ? new CssReading(language, input.Path, input.Text, syntax, scope).Read() : null;
    }

    /// <summary>
    /// The syntax a style block of markup says it is written in: its <c>lang</c> - <c>css</c>, <c>postcss</c>, <c>scss</c>,
    /// <c>sass</c>, <c>less</c> - else its <c>type</c> as HTML writes one - <c>text/css</c>, <c>text/scss</c>,
    /// <c>text/x-scss</c>, <c>text/sass</c>, <c>text/less</c> - and CSS where it says neither; <see langword="null"/> where it
    /// names another (<c>lang="stylus"</c>, a template's type), which no style sheet's reader reads.
    /// </summary>
    private static StyleSyntax? BlockSyntax(string? lang, string? type)
    {
        var named = lang?.Trim() is { Length: > 0 } written ? written.ToLowerInvariant()
            : type?.Trim() is { Length: > 0 } typed ? (typed.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ? typed[5..].ToLowerInvariant() : null)
            : "css";

        return (named is ['x', '-', ..] ? named[2..] : named) switch
        {
            "css" or "postcss" => StyleSyntax.Css,
            "scss" => StyleSyntax.Scss,
            "sass" => StyleSyntax.Sass,
            "less" => StyleSyntax.Less,
            _ => null,
        };
    }

    /// <summary>The syntax a sheet's extension says it is written in: CSS where it says none of the others.</summary>
    private static StyleSyntax SyntaxOf(string path)
        => (path.LastIndexOf('.') is var dot and >= 0 ? path[(dot + 1)..].ToLowerInvariant() : null) switch
        {
            "scss" => StyleSyntax.Scss,
            "sass" => StyleSyntax.Sass,
            "less" => StyleSyntax.Less,
            _ => StyleSyntax.Css,
        };

    /// <summary>
    /// Whether the file at <paramref name="path"/> is a style sheet named as a CSS module is: <c>.module</c> before its
    /// extension, as bundlers read one - the one rule this reader and a script's import of the sheet ask.
    /// </summary>
    internal static bool IsModule(string path)
    {
        var name = CodeGraphIds.FileName(path);
        var dot = name.LastIndexOf('.');
        return dot > 0 && name[..dot].EndsWith(".module", StringComparison.OrdinalIgnoreCase);
    }
}
