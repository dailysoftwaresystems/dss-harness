using DssHarness.Core.Repository;

namespace DssHarness.Core.Codebase;

/// <summary>
/// The shape of a run's one graph, as the workflow's <c>codebaseGraph</c> says it once for the run - every default held
/// where it says nothing: which paths are graphed, which implementations read them, the largest file read, what becomes
/// of generated code and the repository map's cap.
/// </summary>
/// <param name="Include">Only paths one of these matches are graphed; every path where none is given.</param>
/// <param name="Exclude">Paths one of these matches are not graphed.</param>
/// <param name="Languages">The implementations used, by name; every one the registry holds where <see langword="null"/>. A file whose claimant is left out is read by the fallback.</param>
/// <param name="MaxFileBytes">The largest file read; one larger is listed <c>tooLarge</c>.</param>
/// <param name="ExcludeGenerated">Whether a generated file's declarations are left out (<c>exclude</c>) rather than kept, marked and ranked last (<c>low</c>).</param>
/// <param name="MapTokens">The repository map's cap, in tokens; 0 gives no map.</param>
public sealed record CodebaseGraphShape(
    IReadOnlyList<GlobPattern> Include,
    IReadOnlyList<GlobPattern> Exclude,
    IReadOnlyList<string>? Languages,
    long MaxFileBytes,
    bool ExcludeGenerated,
    int MapTokens)
{
    /// <summary>The largest file read where the workflow says nothing: a mebibyte.</summary>
    public const int DefaultMaxFileBytes = 1_048_576;

    /// <summary>The least <c>maxFileBytes</c> may say.</summary>
    public const int LeastMaxFileBytes = 4_096;

    /// <summary>The most <c>maxFileBytes</c> may say.</summary>
    public const int MostMaxFileBytes = 16_777_216;

    /// <summary>The repository map's cap where the workflow says nothing.</summary>
    public const int DefaultMapTokens = 1_500;

    /// <summary>The most <c>mapTokens</c> may say.</summary>
    public const int MostMapTokens = 2_000;

    /// <summary>Generated code's declarations kept, marked and ranked after every other: the default.</summary>
    public const string GeneratedLow = "low";

    /// <summary>Generated code listed and sized, and its declarations left out.</summary>
    public const string GeneratedExclude = "exclude";

    /// <summary>What <c>generated</c> takes.</summary>
    public static IReadOnlyList<string> GeneratedValues { get; } = [GeneratedLow, GeneratedExclude];

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
    public static CodebaseGraphShape Of(
        IReadOnlyList<string>? include,
        IReadOnlyList<string>? exclude,
        IReadOnlyList<string>? languages,
        int? maxFileBytes,
        string? generated,
        int? mapTokens)
        => new(
            [.. (include ?? []).Select(GlobPattern.Parse)],
            [.. (exclude ?? []).Select(GlobPattern.Parse)],
            languages,
            maxFileBytes ?? DefaultMaxFileBytes,
            generated == GeneratedExclude,
            mapTokens ?? DefaultMapTokens);

    /// <summary>Whether <paramref name="path"/> is graphed: matched by an <c>include</c> where any is given, and by no <c>exclude</c>.</summary>
    /// <param name="path">A path relative to the tree, with forward separators.</param>
    /// <param name="comparison">How the tree's file system compares names: a pattern matches a name in any case where it finds one so.</param>
    public bool Graphs(string path, StringComparison comparison)
        => (Include.Count == 0 || Include.Any(pattern => pattern.Matches(path, comparison))) && !Exclude.Any(pattern => pattern.Matches(path, comparison));

    /// <summary>Whether the implementation named <paramref name="language"/> is used.</summary>
    /// <param name="language">An implementation's name.</param>
    public bool Uses(string language) => Languages is null || Languages.Contains(language, StringComparer.Ordinal);
}
