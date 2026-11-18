namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// A style sheet - <c>.css</c>, <c>.scss</c>, <c>.sass</c> in its indented syntax and <c>.less</c> - read over the
/// structural scanner (<see cref="CssLexer"/>, <see cref="CssReading"/>) as the rules it holds: each simple part of each
/// selector - <c>.btn</c>, <c>#main</c>, <c>my-widget</c>, a placeholder's <c>%base</c> - a <c>member</c> SubKind
/// <c>rule</c> named as CSS reads it, over its rule's lines, a nested rule's and one inside an at-rule's block among
/// them; each custom property, <c>@property</c> and SCSS or Less variable a <c>constant</c>; each <c>@mixin</c>,
/// <c>@function</c>, <c>@keyframes</c> and <c>@font-face</c> a member of its own. A <c>var(--x)</c>, a <c>$x</c> and a
/// Less <c>@x</c> are uses of the constant, an <c>@include</c> and a Less mixin call uses of the mixin, an
/// <c>@extend</c> and a Less <c>:extend</c> an inheritance of the rule it names, a Tailwind <c>@apply</c> and a CSS
/// module's <c>composes</c> uses of the rules of its classes, each made from the node that holds it; an
/// <c>@import</c>, <c>@use</c> and <c>@forward</c> naming a file of the tree is an import of it, as the sheet's language
/// finds it. A sheet named as a CSS module (<c>x.module.css</c>) exports its classes, ids and keyframes by their names,
/// so a script's <c>styles.btn</c> binds to <c>.btn</c>.
/// </summary>
public sealed class CssExtractor : ICodeGraphExtractor
{
    /// <inheritdoc/>
    public string Language => "css";

    /// <inheritdoc/>
    public int Version => 2;

    /// <inheritdoc/>
    public CodeGraphClaims Claims { get; } = CodeGraphClaims.OfExtensions(".css", ".scss", ".sass", ".less");

    /// <inheritdoc/>
    public bool Outlines => true;

    /// <inheritdoc/>
    public CodeGraphExtractorHelp Help { get; } = new(
        "Style sheets (.css, .scss, .sass in its indented syntax, .less): each simple part of each selector - a nested rule's, one inside an @media, @supports, @layer or any at-rule's block, and one an & writes a suffix to among them - as a rule node named as CSS reads it, escapes read, over its own rule's lines; each custom property, @property and SCSS or Less variable as a constant; each @mixin, @function, @keyframes and @font-face as a member; a var(--x), a $x and a Less @x as uses of the constant, an @include and a Less mixin call as uses of the mixin, an @extend and a Less :extend as an inheritance of the rule it names, and a Tailwind @apply and a CSS module's composes as uses of the rules of its classes, each made from the rule, mixin or variable that holds it; an @import, @use and @forward naming a file of the tree as an import of it, tried as the sheet's language tries it - Sass's partials, index files and import-only files among them - a @use by its namespace and a @forward with its show and hide; and a sheet named x.module.css (or .scss, .sass, .less) as a CSS module, whose classes, ids and keyframes a script's import binds to by their names, camel-cased too",
        [
            "a pseudo-class's, a pseudo-element's and an attribute's condition, and a :not() or a :has(): no parts of a rule of their own, but an :is()'s and a :where()'s selectors, which are",
            "a rule's declarations, the custom properties and variables among them excepted, and a keyframe's from, to and percentages: no nodes of their own",
            "a selector, a name or a path an interpolation writes (#{...}, Less's @{...}): no node and no import, said as dynamic, what the interpolation uses read",
            "an @import, @use or @forward that names a package, a URL, a host's path or one of Sass's own modules (sass:math): no import of it; a @forward's prefix (as name-*): its names read as they are declared",
            "a call of an @function, a font-family naming an @font-face and an animation naming a @keyframes: no reference of it",
            "a block nested more than 64 deep: not read, said as dynamic",
        ]);

    /// <inheritdoc/>
    public FileExtract Extract(CodeFileInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return new CssReading(Language, input.Path, input.Text, SyntaxOf(input.Path), module: IsModule(input.Path)).Read();
    }

    /// <summary>
    /// Reads a style sheet's text as another implementation's extract - a Vue component's style block among them - every
    /// id prefixed by <paramref name="language"/>, as CSS where <paramref name="syntax"/> says no other; never as a CSS
    /// module, whose names only a sheet of its own exports.
    /// </summary>
    /// <param name="language">The implementation the extract is part of: the prefix of every id.</param>
    /// <param name="input">The sheet's text, at the lines it stands on in its own file.</param>
    /// <param name="syntax">What the sheet is written in - a style block's <c>lang</c>: <c>scss</c>, <c>sass</c> or <c>less</c>; CSS where it is none of them.</param>
    internal static FileExtract Read(string language, CodeFileInput input, string? syntax = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        return new CssReading(language, input.Path, input.Text, SyntaxNamed(syntax), module: false).Read();
    }

    /// <summary>The syntax a sheet's extension says it is written in: CSS where it says none of the others.</summary>
    private static StyleSyntax SyntaxOf(string path)
        => SyntaxNamed(path.LastIndexOf('.') is var dot and >= 0 ? path[(dot + 1)..] : null);

    /// <summary>The syntax <paramref name="name"/> - an extension without its dot, or a style block's <c>lang</c> - names: CSS where it names none of the others.</summary>
    private static StyleSyntax SyntaxNamed(string? name)
        => name?.ToLowerInvariant() switch
        {
            "scss" => StyleSyntax.Scss,
            "sass" => StyleSyntax.Sass,
            "less" => StyleSyntax.Less,
            _ => StyleSyntax.Css,
        };

    /// <summary>Whether the sheet at <paramref name="path"/> is named as a CSS module is: <c>.module</c> before its extension, as bundlers read one.</summary>
    private static bool IsModule(string path)
    {
        var name = CodeGraphIds.FileName(path);
        var dot = name.LastIndexOf('.');
        return dot > 0 && name[..dot].EndsWith(".module", StringComparison.OrdinalIgnoreCase);
    }
}
