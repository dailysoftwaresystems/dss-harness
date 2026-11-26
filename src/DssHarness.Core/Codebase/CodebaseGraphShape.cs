using DssHarness.Core.Repository;

namespace DssHarness.Core.Codebase;

/// <summary>
/// The shape of a run's one graph, as the workflow's <c>codebaseGraph</c> says it once for the run - every default held
/// where it says nothing: which paths are graphed, which implementations read them, what becomes of generated code, the cap of the repository map, the naming policies a configuration key is joined by, and the file
/// name endings the repository maps to an implementation of its own.
/// </summary>
/// <param name="Include">Only paths one of these matches are graphed; every path where none is given.</param>
/// <param name="Exclude">Paths one of these matches are not graphed.</param>
/// <param name="Languages">The implementations used, by name, in ordinal order; every one the registry holds where <see langword="null"/>. A file whose claimant is left out is read by the fallback.</param>
/// <param name="ExcludeGenerated">Whether a generated file's declarations are left out (<c>exclude</c>) rather than kept, its file's entry naming why it is generated (<c>low</c>).</param>
/// <param name="MapTokens">The size, in estimated tokens, of a page of the run's repository map; 0 asks for none.</param>
/// <param name="KeyNaming">
/// The naming policies a configuration key's last segment is joined to a member's name by - <c>camelCase</c>,
/// <c>snake_case</c>, <c>kebab-case</c> and <c>exact</c> - each once, in that order (<see cref="NamingValues"/>):
/// <c>camelCase</c> and <c>exact</c> the default (<see cref="DefaultKeyNaming"/>), which <see cref="Of"/> holds where the
/// workflow says none, and <see langword="null"/> stands for.
/// </param>
/// <param name="Claims">
/// The file name endings, each with its dot, the repository maps to an implementation, by its name - <c>.lang.json</c> to
/// <c>json</c>, say - claimed after an exact name the registry holds and before an extension: each implementation's
/// endings in lower case and ordinal order, as <see cref="Of"/> holds them, a file's name read in any case; null where the
/// repository maps none.
/// </param>
public sealed record CodebaseGraphShape(
    IReadOnlyList<GlobPattern> Include,
    IReadOnlyList<GlobPattern> Exclude,
    IReadOnlyList<string>? Languages,
    bool ExcludeGenerated,
    int MapTokens,
    IReadOnlyList<string>? KeyNaming = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Claims = null)
{
    /// <summary>The size of a page of the run's repository map where the workflow says nothing.</summary>
    public const int DefaultMapTokens = 1_500;

    /// <summary>The most <c>mapTokens</c> may say.</summary>
    public const int MostMapTokens = 2_000;

    /// <summary>Generated code's declarations kept, its file's entry naming why it is generated: the default.</summary>
    public const string GeneratedLow = "low";

    /// <summary>Generated code listed and sized, and its declarations left out.</summary>
    public const string GeneratedExclude = "exclude";

    /// <summary>What <c>generated</c> takes.</summary>
    public static IReadOnlyList<string> GeneratedValues { get; } = [GeneratedLow, GeneratedExclude];

    /// <summary>A naming policy of <c>keyNaming</c>: the key is the member's name with its first letter lowered.</summary>
    public const string CamelCase = "camelCase";

    /// <summary>A naming policy of <c>keyNaming</c>: the key is the member's name with its capitals lowered and an underscore set before each past the first.</summary>
    public const string SnakeCase = "snake_case";

    /// <summary>A naming policy of <c>keyNaming</c>: the key is the member's name with its capitals lowered and a dash set before each past the first.</summary>
    public const string KebabCase = "kebab-case";

    /// <summary>A naming policy of <c>keyNaming</c>: the key is the member's name as it is.</summary>
    public const string Exact = "exact";

    /// <summary>What <c>keyNaming</c> may say.</summary>
    public static IReadOnlyList<string> NamingValues { get; } = [CamelCase, SnakeCase, KebabCase, Exact];

    /// <summary>The naming policies where the workflow says nothing: a camelCase key is a PascalCase member's, and an exact name is its own.</summary>
    public static IReadOnlyList<string> DefaultKeyNaming { get; } = [CamelCase, Exact];

    /// <summary>
    /// The naming policies a key's last segment is joined by, as <paramref name="keyNaming"/> says them, each once in the
    /// order <see cref="NamingValues"/> lists them: the default where it is <see langword="null"/>.
    /// </summary>
    /// <param name="keyNaming">The policies a shape holds, or null for the default.</param>
    /// <exception cref="ArgumentException"><paramref name="keyNaming"/> names none, one twice, or one the graph joins no key by: what the definition's check and a manifest's refuse.</exception>
    public static IReadOnlyList<string> NamingOf(IReadOnlyList<string>? keyNaming)
        => keyNaming is null ? DefaultKeyNaming
            : NamingProblem(keyNaming) is { } wrong ? throw new ArgumentException($"the key naming {wrong}", nameof(keyNaming))
            : Named(keyNaming);

    /// <summary>
    /// The first thing wrong with <paramref name="keyNaming"/> as the policies a key is joined by (<see cref="NamingProblems"/>),
    /// said to follow its subject; <see langword="null"/> where nothing is.
    /// </summary>
    /// <param name="keyNaming">The policies a block or a manifest names.</param>
    public static string? NamingProblem(IReadOnlyList<string> keyNaming)
        => NamingProblems(keyNaming) is [var first, ..] ? Worded(first) : null;

    /// <summary>
    /// Everything wrong with <paramref name="keyNaming"/> as the policies a key is joined by, in order: it names none; it
    /// names one more than once, each such once; it names one the graph joins no key by, each such once. The one rule of a
    /// sound <c>keyNaming</c>, which the definition's check words for its block and a manifest's and a shape's read whole.
    /// </summary>
    /// <param name="keyNaming">The policies a block or a manifest names.</param>
    public static IReadOnlyList<(KeyNamingFault Fault, string? Naming)> NamingProblems(IReadOnlyList<string> keyNaming)
    {
        ArgumentNullException.ThrowIfNull(keyNaming);

        if (keyNaming.Count == 0)
        {
            return [(KeyNamingFault.None, null)];
        }

        return
        [
            .. keyNaming.GroupBy(naming => naming, StringComparer.Ordinal).Where(named => named.Count() > 1).Select(named => (KeyNamingFault.Twice, (string?)named.Key)),
            .. keyNaming.Where(naming => !NamingValues.Contains(naming, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).Select(naming => (KeyNamingFault.Unknown, (string?)naming)),
        ];
    }

    /// <summary>A fault of <see cref="NamingProblems"/>, said to follow its subject.</summary>
    private static string Worded((KeyNamingFault Fault, string? Naming) problem) => problem.Fault switch
    {
        KeyNamingFault.None => "names no policy",
        KeyNamingFault.Twice => $"names '{problem.Naming}' more than once",
        _ => $"names '{problem.Naming}', which is none of {NamingList}",
    };

    /// <summary>Every policy <c>keyNaming</c> may say, each quoted, as a refusal lists them.</summary>
    public static string NamingList => string.Join(", ", NamingValues.Select(value => $"'{value}'"));

    /// <summary>
    /// <paramref name="keyNaming"/> each once: those of <see cref="NamingValues"/> in its order, then any other in the order
    /// written - which the definition's check refuses, kept so a shape that names it is no other shape.
    /// </summary>
    private static IReadOnlyList<string> Named(IReadOnlyList<string> keyNaming)
        => [.. NamingValues.Where(naming => keyNaming.Contains(naming, StringComparer.Ordinal)), .. keyNaming.Where(naming => !NamingValues.Contains(naming, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// <paramref name="claims"/> as a shape holds them: each implementation's endings in lower case, each once in ordinal
    /// order, the implementations in ordinal order - a file's name read in any case, two that map alike one map; null
    /// where it maps none.
    /// </summary>
    /// <param name="claims">An implementation's name to the endings it claims, as a block or a manifest writes them, or null.</param>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>>? ClaimsOf(IReadOnlyDictionary<string, IReadOnlyList<string>>? claims)
        => claims is null ? null
            : new SortedDictionary<string, IReadOnlyList<string>>(
                claims.ToDictionary(claim => claim.Key, claim => (IReadOnlyList<string>)[.. claim.Value.Select(ending => ending.ToLowerInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)], StringComparer.Ordinal),
                StringComparer.Ordinal);

    /// <summary>Whether <paramref name="one"/> and <paramref name="other"/> map the same endings to the same implementations, as <see cref="ClaimsOf"/> holds them: the one owner of whether two claims are one.</summary>
    /// <param name="one">Claims, or null for none.</param>
    /// <param name="other">Other claims, or null for none.</param>
    public static bool SameClaims(IReadOnlyDictionary<string, IReadOnlyList<string>>? one, IReadOnlyDictionary<string, IReadOnlyList<string>>? other)
    {
        var (first, second) = (ClaimsOf(one), ClaimsOf(other));

        return first is null ? second is null
            : second is not null && first.Count == second.Count && first.All(claim => second.TryGetValue(claim.Key, out var endings) && claim.Value.SequenceEqual(endings, StringComparer.Ordinal));
    }

    /// <summary>
    /// Whether this shape and <paramref name="other"/> make one graph: the same patterns, implementations, generated code,
    /// map, naming policies and claims, each as a shape holds it - a default said and one left out alike,
    /// two lists of one set alike. The one owner of whether two shapes are one.
    /// </summary>
    /// <param name="other">Another shape.</param>
    public bool SameAs(CodebaseGraphShape other)
    {
        ArgumentNullException.ThrowIfNull(other);

        static bool Same(IReadOnlyList<string>? one, IReadOnlyList<string>? two)
            => one is null ? two is null : two is not null && one.Order(StringComparer.Ordinal).SequenceEqual(two.Order(StringComparer.Ordinal), StringComparer.Ordinal);

        return Include.Select(pattern => pattern.ToString()).SequenceEqual(other.Include.Select(pattern => pattern.ToString()), StringComparer.Ordinal)
            && Exclude.Select(pattern => pattern.ToString()).SequenceEqual(other.Exclude.Select(pattern => pattern.ToString()), StringComparer.Ordinal)
            && Same(Languages, other.Languages)
            && ExcludeGenerated == other.ExcludeGenerated
            && MapTokens == other.MapTokens
            && Named(KeyNaming ?? DefaultKeyNaming).SequenceEqual(Named(other.KeyNaming ?? DefaultKeyNaming), StringComparer.Ordinal)
            && SameClaims(Claims, other.Claims);
    }

    /// <summary>The shape where the workflow says nothing of the graph.</summary>
    public static CodebaseGraphShape Default { get; } = new([], [], null, ExcludeGenerated: false, DefaultMapTokens);

    /// <summary>
    /// The shape the workflow's block says, each key it leaves out at its default: the patterns read as
    /// <see cref="GlobPattern"/> reads them, which the definition's check held them to before any run; the implementations,
    /// the naming policies and the claims each held in one order, so two blocks that say one set alike give one shape.
    /// </summary>
    /// <param name="include">The block's <c>include</c>, or <see langword="null"/>.</param>
    /// <param name="exclude">The block's <c>exclude</c>, or <see langword="null"/>.</param>
    /// <param name="languages">The block's <c>languages</c>, or <see langword="null"/>.</param>
    /// <param name="generated">The block's <c>generated</c>, or <see langword="null"/>.</param>
    /// <param name="mapTokens">The block's <c>mapTokens</c>, or <see langword="null"/>.</param>
    /// <param name="keyNaming">The block's <c>keyNaming</c>, or <see langword="null"/>.</param>
    /// <param name="claims">The block's <c>claims</c>, an implementation's name to the endings it claims, or <see langword="null"/>.</param>
    public static CodebaseGraphShape Of(
        IReadOnlyList<string>? include,
        IReadOnlyList<string>? exclude,
        IReadOnlyList<string>? languages,
        string? generated,
        int? mapTokens,
        IReadOnlyList<string>? keyNaming = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? claims = null)
        => new(
            [.. (include ?? []).Select(GlobPattern.Parse)],
            [.. (exclude ?? []).Select(GlobPattern.Parse)],
            languages is null ? null : [.. languages.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            generated == GeneratedExclude,
            mapTokens ?? DefaultMapTokens,
            Named(keyNaming ?? DefaultKeyNaming),
            ClaimsOf(claims));

    /// <summary>Whether <paramref name="path"/> is graphed: matched by an <c>include</c> where any is given, and by no <c>exclude</c>.</summary>
    /// <param name="path">A path relative to the tree, with forward separators.</param>
    /// <param name="comparison">How the tree's file system compares names: a pattern matches a name in any case where it finds one so.</param>
    public bool Graphs(string path, StringComparison comparison)
        => (Include.Count == 0 || Include.Any(pattern => pattern.Matches(path, comparison))) && !Exclude.Any(pattern => pattern.Matches(path, comparison));

    /// <summary>Whether the implementation named <paramref name="language"/> is used.</summary>
    /// <param name="language">An implementation's name.</param>
    public bool Uses(string language) => Languages is null || Languages.Contains(language, StringComparer.Ordinal);
}

/// <summary>What is wrong with a <c>keyNaming</c> list (<see cref="CodebaseGraphShape.NamingProblems"/>).</summary>
public enum KeyNamingFault
{
    /// <summary>It names no policy.</summary>
    None,

    /// <summary>It names a policy more than once.</summary>
    Twice,

    /// <summary>It names one the graph joins no key by.</summary>
    Unknown,
}
