using DssHarness.Core.Repository;

namespace DssHarness.Core.Codebase;

/// <summary>
/// The shape of a run's one graph, as the workflow's <c>codebaseGraph</c> says it once for the run - every default held
/// where it says nothing: which paths are graphed, which implementations read them, the largest file read, what becomes
/// of generated code, the cap of the repository map, the naming policies a configuration key is joined by, and the file
/// name endings the repository maps to an implementation of its own.
/// </summary>
/// <param name="Include">Only paths one of these matches are graphed; every path where none is given.</param>
/// <param name="Exclude">Paths one of these matches are not graphed.</param>
/// <param name="Languages">The implementations used, by name; every one the registry holds where <see langword="null"/>. A file whose claimant is left out is read by the fallback.</param>
/// <param name="MaxFileBytes">The largest file read; one larger is listed <c>tooLarge</c>.</param>
/// <param name="ExcludeGenerated">Whether a generated file's declarations are left out (<c>exclude</c>) rather than kept, its file's entry naming why it is generated (<c>low</c>).</param>
/// <param name="MapTokens">The cap, in tokens, of the run's repository map; 0 asks for none.</param>
/// <param name="KeyNaming">The naming policies a configuration key's last segment is joined to a member's name by: <c>camelCase</c>, <c>snake_case</c>, <c>kebab-case</c> and <c>exact</c>, the last two the default.</param>
/// <param name="Claims">The file name endings, each with its dot, the repository maps to an implementation, by its name - <c>.lang.json</c> to <c>json</c>, say - claimed after an exact name the registry holds and before an extension: null where the repository maps none.</param>
public sealed record CodebaseGraphShape(
    IReadOnlyList<GlobPattern> Include,
    IReadOnlyList<GlobPattern> Exclude,
    IReadOnlyList<string>? Languages,
    long MaxFileBytes,
    bool ExcludeGenerated,
    int MapTokens,
    IReadOnlyList<string>? KeyNaming = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Claims = null)
{
    /// <summary>The largest file read where the workflow says nothing: a mebibyte.</summary>
    public const int DefaultMaxFileBytes = 1_048_576;

    /// <summary>The least <c>maxFileBytes</c> may say.</summary>
    public const int LeastMaxFileBytes = 4_096;

    /// <summary>The most <c>maxFileBytes</c> may say.</summary>
    public const int MostMaxFileBytes = 16_777_216;

    /// <summary>The cap of the run's repository map where the workflow says nothing.</summary>
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
    /// The naming policies a key's last segment is joined by, as <paramref name="keyNaming"/> says them - the default
    /// where it says none, or says nothing the graph reads.
    /// </summary>
    /// <param name="keyNaming">What the workflow's <c>keyNaming</c> says, or null.</param>
    public static IReadOnlyList<string> NamingOf(IReadOnlyList<string>? keyNaming)
        => keyNaming is { Count: > 0 } && keyNaming.All(naming => NamingValues.Contains(naming, StringComparer.Ordinal)) ? keyNaming : DefaultKeyNaming;

    /// <summary>The shape where the workflow says nothing of the graph.</summary>
    public static CodebaseGraphShape Default { get; } = new([], [], null, DefaultMaxFileBytes, ExcludeGenerated: false, DefaultMapTokens);

    /// <summary>
    /// The shape the workflow's block says, each key it leaves out at its default: the patterns read as
    /// <see cref="GlobPattern"/> reads them, which the definition's check held them to before any run.
    /// </summary>
    /// <param name="include">The block's <c>include</c>, or <see langword="null"/>.</param>
    /// <param name="exclude">The block's <c>exclude</c>, or <see langword="null"/>.</param>
    /// <param name="languages">The block's <c>languages</c>, or <see langword="null"/>.</param>
    /// <param name="maxFileBytes">The block's <c>maxFileBytes</c>, or <see langword="null"/>.</param>
    /// <param name="generated">The block's <c>generated</c>, or <see langword="null"/>.</param>
    /// <param name="mapTokens">The block's <c>mapTokens</c>, or <see langword="null"/>.</param>
    /// <param name="keyNaming">The block's <c>keyNaming</c>, or <see langword="null"/>.</param>
    /// <param name="claims">The block's <c>claims</c>, an implementation's name to the endings it claims, or <see langword="null"/>.</param>
    public static CodebaseGraphShape Of(
        IReadOnlyList<string>? include,
        IReadOnlyList<string>? exclude,
        IReadOnlyList<string>? languages,
        int? maxFileBytes,
        string? generated,
        int? mapTokens,
        IReadOnlyList<string>? keyNaming = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? claims = null)
        => new(
            [.. (include ?? []).Select(GlobPattern.Parse)],
            [.. (exclude ?? []).Select(GlobPattern.Parse)],
            languages,
            maxFileBytes ?? DefaultMaxFileBytes,
            generated == GeneratedExclude,
            mapTokens ?? DefaultMapTokens,
            keyNaming,
            claims);

    /// <summary>Whether <paramref name="path"/> is graphed: matched by an <c>include</c> where any is given, and by no <c>exclude</c>.</summary>
    /// <param name="path">A path relative to the tree, with forward separators.</param>
    /// <param name="comparison">How the tree's file system compares names: a pattern matches a name in any case where it finds one so.</param>
    public bool Graphs(string path, StringComparison comparison)
        => (Include.Count == 0 || Include.Any(pattern => pattern.Matches(path, comparison))) && !Exclude.Any(pattern => pattern.Matches(path, comparison));

    /// <summary>Whether the implementation named <paramref name="language"/> is used.</summary>
    /// <param name="language">An implementation's name.</param>
    public bool Uses(string language) => Languages is null || Languages.Contains(language, StringComparer.Ordinal);

    /// <summary>
    /// The implementation the repository's <c>claims</c> map gives the file at <paramref name="path"/> by the longest
    /// ending its name ends with, where its name is none the registry holds exactly and the map gives one; null where
    /// none does.
    /// </summary>
    /// <param name="path">A file's path, relative to the tree's root with forward separators.</param>
    public string? EndingClaimOf(string path)
    {
        if (Claims is not { Count: > 0 } claims)
        {
            return null;
        }

        var name = CodeGraphIds.FileName(path);
        var (longest, language) = (0, null as string);

        foreach (var (claimed, endings) in claims)
        {
            foreach (var ending in endings)
            {
                if (ending.Length > longest && name.EndsWith(ending, StringComparison.OrdinalIgnoreCase))
                {
                    (longest, language) = (ending.Length, claimed);
                }
            }
        }

        return language;
    }
}
