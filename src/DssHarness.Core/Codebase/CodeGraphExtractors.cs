using System.Globalization;
using DssHarness.Core.Output;
using DssHarness.Core.Results;

namespace DssHarness.Core.Codebase;

/// <summary>
/// The one owner of the implementations of <see cref="ICodeGraphExtractor"/>: which reads a file, the names
/// <c>codebaseGraph.languages</c> may hold, <c>help codebase-graph</c>'s table, and each extract's name on disk all come
/// from it and from nothing else.
/// </summary>
/// <remarks>
/// <para>
/// Built from every implementation the container holds, which <see cref="Implementations"/> - one scan of the core - gives
/// it; so a new language is registered by being written. Two implementations that would claim one file without a test to
/// tell them apart are refused when it is built, never decided by an order: an order nobody reads in the two classes would
/// let a new language take files from another without a word, and change the graph under every repository at the next
/// release. A test builds it from the real container on every run of the suite, so a clash is a red test in the change
/// that brings it.
/// </para>
/// <para>
/// An exact name is narrower than an extension, so at a file the claimants of its exact name read it, else those of its
/// extension, else the fallback - the contract's order, not the registry's - and where several claim it at that level,
/// their content tests decide.
/// </para>
/// </remarks>
public sealed class CodeGraphExtractors
{
    private static CodeGraphExtractors? _thisBuild;

    private readonly ICodeGraphExtractor _fallback;

    /// <summary>Builds the registry of <paramref name="extractors"/>, refusing what would make a file's claimant a matter of order.</summary>
    /// <param name="extractors">Every implementation, each once.</param>
    /// <exception cref="HarnessException">
    /// Two implementations share a <see cref="ICodeGraphExtractor.Language"/>, or claim one exact name or one extension of
    /// which either lacks a content test; there is no fallback, or more than one; a version is below 1; a name is not one
    /// a node id and a file name can carry (<see cref="HarnessExit.InternalError"/>: a defect of this build).
    /// </exception>
    public CodeGraphExtractors(IEnumerable<ICodeGraphExtractor> extractors)
    {
        ArgumentNullException.ThrowIfNull(extractors);

        var all = extractors.OrderBy(extractor => extractor.Language, StringComparer.Ordinal).ToList();

        Refuse(all.GroupBy(extractor => extractor.Language, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1) is { } twice
            ? $"two implementations of the codebase graph's contract are named '{twice.Key}': {Types(twice)}; a language's name is an id's prefix and an extract's name, and is one implementation's"
            : null);

        foreach (var extractor in all)
        {
            Refuse(!IsName(extractor.Language) ? $"'{extractor.Language}' ({extractor.GetType().FullName}) is not a language's name: lower-case letters and digits, beginning with a letter, and not '{CodeNodeKinds.File}', which names a file's node"
                : extractor.Version < 1 ? $"'{extractor.Language}' is at version {extractor.Version.ToString(CultureInfo.InvariantCulture)}; a version is a positive integer, raised whenever what the implementation answers changes"
                : null);
        }

        foreach (var extractor in all)
        {
            Refuse(extractor.Claims.Names.FirstOrDefault(claim => !IsFileName(claim.Value)) is { } name
                ? $"'{extractor.Language}' claims the file name '{name.Value}', which no file is named: a name holds no separator, and is no '.' or '..'"
                : extractor.Claims.Extensions.FirstOrDefault(claim => !IsExtension(claim.Value)) is { } extension
                ? $"'{extractor.Language}' claims the extension '{extension.Value}', which no file has: an extension is a dot and what follows a name's last dot, with no separator"
                : extractor.Claims.Fallback && (extractor.Claims.Names.Count > 0 || extractor.Claims.Extensions.Count > 0)
                ? $"'{extractor.Language}' is the fallback and claims names or extensions too: the fallback reads what no other claims, and claims nothing"
                : null);
        }

        var fallbacks = all.Where(extractor => extractor.Claims.Fallback).ToList();

        Refuse(fallbacks.Count switch
        {
            0 => "no implementation of the codebase graph's contract is the fallback, and a file no other claims would be read by none: one is, and lists every file no other reads",
            1 => null,
            _ => $"{ReportText.Listed([.. fallbacks.Select(extractor => $"'{extractor.Language}'")])} are each the fallback; one implementation reads every file no other claims",
        });

        foreach (var (what, claimsOf) in new (string, Func<ICodeGraphExtractor, IReadOnlyList<CodeGraphClaim>>)[] { ("file name", extractor => extractor.Claims.Names), ("extension", extractor => extractor.Claims.Extensions) })
        {
            var shared = all
                .SelectMany(extractor => claimsOf(extractor).Select(claim => (Extractor: extractor, Claim: claim)))
                .GroupBy(claimed => claimed.Claim.Value, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1 && group.Any(claimed => claimed.Claim.ContentTest is null));

            Refuse(shared is null ? null
                : $"{ReportText.Listed([.. shared.Select(claimed => $"'{claimed.Extractor.Language}'")])} each claim the {what} '{shared.Key}', and {ReportText.Listed([.. shared.Where(claimed => claimed.Claim.ContentTest is null).Select(claimed => $"'{claimed.Extractor.Language}'")])} give{(shared.Count(claimed => claimed.Claim.ContentTest is null) == 1 ? "s" : string.Empty)} it no content test: two that claim one {what} each say by a test of a file's first bytes which file is theirs, and a file is never given to one by order");
        }

        All = all;
        _fallback = fallbacks[0];
    }

    /// <summary>
    /// Every concrete type of the core that implements <see cref="ICodeGraphExtractor"/>, by its full name: the one scan
    /// that finds them, which the composition root registers each of and the registry of this build is made from.
    /// </summary>
    public static IReadOnlyList<Type> Implementations { get; } =
        [.. typeof(ICodeGraphExtractor).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(ICodeGraphExtractor).IsAssignableFrom(type))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)];

    /// <summary>
    /// The registry of this build's own implementations, each made with no argument as the container makes it: for what
    /// has no container to ask - <c>help codebase-graph</c>.
    /// </summary>
    public static CodeGraphExtractors OfThisBuild
        => LazyInitializer.EnsureInitialized(ref _thisBuild, () => new([.. Implementations.Select(type => (ICodeGraphExtractor)Activator.CreateInstance(type)!)]));

    /// <summary>Every implementation, by its name.</summary>
    public IReadOnlyList<ICodeGraphExtractor> All { get; }

    /// <summary>
    /// The names <c>codebaseGraph.languages</c> may hold: every implementation but the fallback, which reads every file
    /// no other claims and is always used.
    /// </summary>
    public IReadOnlyList<string> Languages => [.. All.Where(extractor => !extractor.Claims.Fallback).Select(extractor => extractor.Language)];

    /// <summary>The implementation that reads every file no other claims.</summary>
    public ICodeGraphExtractor Fallback => _fallback;

    /// <summary>The implementation named <paramref name="language"/>, or <see langword="null"/> where none is.</summary>
    /// <param name="language">A language's name.</param>
    public ICodeGraphExtractor? Find(string language)
        => All.FirstOrDefault(extractor => string.Equals(extractor.Language, language, StringComparison.Ordinal));

    /// <summary>
    /// The implementations that claim <paramref name="path"/> at the narrowest level any does - its exact name, else its
    /// extension - before any content test is asked; none where only the fallback reads it.
    /// </summary>
    /// <param name="path">The file's path, relative to the tree's root with forward separators.</param>
    public IReadOnlyList<(ICodeGraphExtractor Extractor, CodeGraphClaim Claim)> ClaimantsOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var name = CodeGraphIds.FileName(path);
        var byName = Claiming(extractor => extractor.Claims.Names, claim => string.Equals(claim.Value, name, StringComparison.OrdinalIgnoreCase));

        if (byName.Count > 0)
        {
            return byName;
        }

        var dot = name.LastIndexOf('.');

        return dot <= 0
            ? []
            : Claiming(extractor => extractor.Claims.Extensions, claim => string.Equals(claim.Value, name[dot..], StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Which implementation reads <paramref name="path"/>, whose text begins with <paramref name="probe"/>: the one
    /// claimant whose content test says yes - a claim with none says yes - or the fallback where none claims it. Where
    /// none or several of its claimants say yes, no implementation reads it, and it is listed <c>ambiguous</c>.
    /// </summary>
    /// <param name="path">The file's path, relative to the tree's root with forward separators.</param>
    /// <param name="probe">Its first bytes as text (<see cref="CodeFileText.Probe"/>).</param>
    public CodeGraphRoute Route(string path, string probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        var claimants = ClaimantsOf(path);

        if (claimants.Count == 0)
        {
            return new CodeGraphRoute(_fallback, null);
        }

        var yes = new List<ICodeGraphExtractor>();

        foreach (var (extractor, claim) in claimants)
        {
            bool says;

            try
            {
                says = claim.ContentTest is not { } test || test(probe);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The contract's kit holds every content test to answering for any text; one that throws is a defect of its
                // implementation, said for the one file.
                return new CodeGraphRoute(null, null) { Failed = $"its implementation '{extractor.Language}' failed on it in the content test of its claim '{claim.Value}': {ex.Message.TrimEnd('.')}" };
            }

            if (says)
            {
                yes.Add(extractor);
            }
        }

        return yes.Count == 1
            ? new CodeGraphRoute(yes[0], null)
            : new CodeGraphRoute(null, yes.Count == 0
                ? $"{ReportText.Listed([.. claimants.Select(claimed => $"'{claimed.Extractor.Language}'")])} claim it, and none of their content tests says it is theirs"
                : $"{ReportText.Listed([.. yes.Select(extractor => $"'{extractor.Language}'")])} each say by their content tests that it is theirs");
    }

    /// <summary>Whether <paramref name="language"/> is a name a node id and an extract's file name can carry.</summary>
    private static bool IsName(string? language)
        => language is { Length: > 0 }
            && char.IsAsciiLetterLower(language[0])
            && language.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character))
            && language != CodeNodeKinds.File;

    /// <summary>Whether <paramref name="value"/> is a name a file can have: its whole last part.</summary>
    private static bool IsFileName(string? value)
        => value is { Length: > 0 } && value is not "." and not ".." && value.IndexOfAny(['/', '\\']) < 0;

    /// <summary>Whether <paramref name="value"/> is what <see cref="ClaimantsOf"/> reads as an extension: a dot and at least one character after a name's last dot.</summary>
    private static bool IsExtension(string? value)
        => value is { Length: > 1 } && value[0] == '.' && value.IndexOf('.', 1) < 0 && IsFileName(value);

    private static string Types(IEnumerable<ICodeGraphExtractor> extractors)
        => string.Join(", ", extractors.Select(extractor => extractor.GetType().FullName));

    private static void Refuse(string? problem)
    {
        if (problem is not null)
        {
            throw new HarnessException(HarnessExit.InternalError, $"The codebase graph's implementations do not hold together: {problem}.");
        }
    }

    private List<(ICodeGraphExtractor Extractor, CodeGraphClaim Claim)> Claiming(
        Func<ICodeGraphExtractor, IReadOnlyList<CodeGraphClaim>> claimsOf,
        Func<CodeGraphClaim, bool> matches)
        => [.. All.SelectMany(extractor => claimsOf(extractor).Where(matches).Select(claim => (extractor, claim)))];
}

/// <summary>Which implementation reads a file, or why none does.</summary>
/// <param name="Extractor">The implementation; <see langword="null"/> where the file is ambiguous, or a content test failed.</param>
/// <param name="Ambiguous">Why no implementation reads it, its claimants' tests saying none or several; <see langword="null"/> where they said one.</param>
public sealed record CodeGraphRoute(ICodeGraphExtractor? Extractor, string? Ambiguous)
{
    /// <summary>Why no implementation reads it, a content test of a claimant having failed on it; <see langword="null"/> where none did.</summary>
    public string? Failed { get; init; }
}
