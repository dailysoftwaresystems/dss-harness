using System.Globalization;
using DssHarness.Core.Output;
using DssHarness.Core.Results;

namespace DssHarness.Core.Codebase;

/// <summary>
/// The one owner of the implementations of <see cref="ICodeGraphExtractor"/>: which reads a file, the names
/// <c>codebaseGraph.languages</c> may hold, <c>help codebase-graph-languages</c>'s table, and each extract's name on disk all come
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
    /// a node id and a file name can carry; a limit's short name is none, or two limits of one implementation share one
    /// (<see cref="CodeGraphLimit.Name"/>) (<see cref="HarnessExit.InternalError"/>: a defect of this build).
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
                : extractor.Help.DoesNotSee.FirstOrDefault(limit => !CodeGraphLimit.IsName(limit.Name)) is { } unnamed
                ? $"'{extractor.Language}' names a limit '{unnamed.Name}', which is no short name: lower-case letters, digits and dashes, beginning with a letter and ending with no dash - the name the help lists it under and an answer names it by"
                : extractor.Help.DoesNotSee.GroupBy(limit => limit.Name, StringComparer.Ordinal).FirstOrDefault(named => named.Count() > 1) is { } shared
                ? $"'{extractor.Language}' names {shared.Count().ToString(CultureInfo.InvariantCulture)} of its limits '{shared.Key}': each is named once, so the name an answer says finds one in the help"
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
            _ => $"{Named(fallbacks)} are each the fallback; one implementation reads every file no other claims",
        });

        foreach (var (what, claimsOf) in new (string, Func<ICodeGraphExtractor, IReadOnlyList<CodeGraphClaim>>)[] { ("file name", extractor => extractor.Claims.Names), ("extension", extractor => extractor.Claims.Extensions) })
        {
            var shared = all
                .SelectMany(extractor => claimsOf(extractor).Select(claim => (Extractor: extractor, Claim: claim)))
                .GroupBy(claimed => claimed.Claim.Value, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1 && group.Any(claimed => claimed.Claim.ContentTest is null));

            Refuse(shared is null ? null
                : $"{Named([.. shared.Select(claimed => claimed.Extractor)])} each claim the {what} '{shared.Key}', and {Named([.. shared.Where(claimed => claimed.Claim.ContentTest is null).Select(claimed => claimed.Extractor)])} give{(shared.Count(claimed => claimed.Claim.ContentTest is null) == 1 ? "s" : string.Empty)} it no content test: two that claim one {what} each say by a test of a file's text which file is theirs, and a file is never given to one by order");
        }

        All = all;
        _fallback = fallbacks[0];
    }

    /// <summary>
    /// Every concrete type of the core that implements <see cref="ICodeGraphExtractor"/>, by its full name: the one scan
    /// that finds them (<see cref="CoreImplementations{T}"/>), which the composition root registers each of and
    /// the registry of this build is made from.
    /// </summary>
    public static IReadOnlyList<Type> Implementations => CoreImplementations<ICodeGraphExtractor>.Types;

    /// <summary>
    /// The registry of this build's own implementations, each made with no argument as the container makes it: for what
    /// has no container to ask - the help topics <c>codebase-graph</c> and <c>codebase-graph-languages</c>.
    /// </summary>
    public static CodeGraphExtractors OfThisBuild
        => LazyInitializer.EnsureInitialized(ref _thisBuild, () => new(CoreImplementations<ICodeGraphExtractor>.OfThisBuild));

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
    /// extension - before any content test is asked; none where only the fallback reads it. Where
    /// <paramref name="endingClaims"/> maps endings of a file's name to implementations, the longest of them its name
    /// ends with is claimed between the two, after any exact name and before any extension.
    /// </summary>
    /// <param name="path">The file's path, relative to the tree's root with forward separators.</param>
    /// <param name="endingClaims">The endings a repository maps to implementations, by name, or null where it maps none.</param>
    public IReadOnlyList<(ICodeGraphExtractor Extractor, CodeGraphClaim Claim)> ClaimantsOf(string path, IReadOnlyDictionary<string, IReadOnlyList<string>>? endingClaims = null)
    {
        ArgumentNullException.ThrowIfNull(path);

        var name = CodeGraphIds.FileName(path);
        var byName = Claiming(extractor => extractor.Claims.Names, claim => string.Equals(claim.Value, name, StringComparison.OrdinalIgnoreCase));

        if (byName.Count > 0)
        {
            return byName;
        }

        if (EndingOf(name, endingClaims) is { } byEnding)
        {
            return byEnding;
        }

        var dot = name.LastIndexOf('.');

        return dot <= 0
            ? []
            : Claiming(extractor => extractor.Claims.Extensions, claim => string.Equals(claim.Value, name[dot..], StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The claimants of the longest ending <paramref name="name"/> ends with among <paramref name="endingClaims"/>, each
    /// mapped ending to the implementation its language names; none where no mapped ending matches. A mapped language
    /// this build holds no implementation of is left out, as its workflow's check refuses one before any run.
    /// </summary>
    private List<(ICodeGraphExtractor Extractor, CodeGraphClaim Claim)>? EndingOf(string name, IReadOnlyDictionary<string, IReadOnlyList<string>>? endingClaims)
    {
        if (endingClaims is not { Count: > 0 })
        {
            return null;
        }

        var (longest, of) = (0, null as List<(ICodeGraphExtractor, CodeGraphClaim)>);

        foreach (var (language, endings) in endingClaims)
        {
            if (Find(language) is not { } extractor)
            {
                continue;
            }

            foreach (var ending in endings)
            {
                if (ending.Length == 0 || !name.EndsWith(ending, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (ending.Length > longest || of is null)
                {
                    of = [];
                    longest = ending.Length;
                }

                if (ending.Length == longest)
                {
                    of.Add((extractor, new CodeGraphClaim(ending)));
                }
            }
        }

        return of;
    }

    /// <summary>
    /// Which implementation reads <paramref name="path"/>, whose text is <paramref name="probe"/>: the one
    /// claimant whose content test says yes - a claim with none says yes - or the fallback where none claims it. Where
    /// none or several of its claimants say yes, no implementation reads it, and it is listed <c>ambiguous</c>.
    /// </summary>
    /// <param name="path">The file's path, relative to the tree's root with forward separators.</param>
    /// <param name="probe">Its decoded text, whole (<see cref="CodeText"/>): each test reads of it as far as it needs.</param>
    /// <param name="endingClaims">The endings a repository maps to implementations, by name, or null where it maps none.</param>
    public CodeGraphRoute Route(string path, CodeText probe, IReadOnlyDictionary<string, IReadOnlyList<string>>? endingClaims = null)
    {
        ArgumentNullException.ThrowIfNull(probe);

        var claimants = ClaimantsOf(path, endingClaims);

        if (claimants.Count == 0)
        {
            return CodeGraphRoute.To(_fallback);
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
                return CodeGraphRoute.FailedFor($"its implementation '{extractor.Language}' failed on it in the content test of its claim '{claim.Value}': {ex.Message.TrimEnd('.')}");
            }

            if (says)
            {
                yes.Add(extractor);
            }
        }

        return yes.Count == 1
            ? CodeGraphRoute.To(yes[0])
            : CodeGraphRoute.AmbiguousFor(yes.Count == 0
                ? $"{Named([.. claimants.Select(claimed => claimed.Extractor)])} claim it, and none of their content tests says it is theirs"
                : $"{Named(yes)} each say by their content tests that it is theirs");
    }

    /// <summary>
    /// <paramref name="extractors"/> named in one line, each quoted: every one that fits, and for the rest how many and
    /// where every implementation and its claims are listed - <c>help codebase-graph-languages</c> (<see cref="Names.Line"/>).
    /// </summary>
    private static string Named(IReadOnlyList<ICodeGraphExtractor> extractors)
        => Names.Line([.. extractors.Select(extractor => $"'{extractor.Language}'")], $"help {Help.HelpTopics.CodebaseGraphLanguages}");

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

/// <summary>
/// Which implementation reads a file, or why none does - one of the three, as <see cref="To"/>, <see cref="AmbiguousFor"/>
/// and <see cref="FailedFor"/> alone make it.
/// </summary>
public sealed record CodeGraphRoute
{
    private CodeGraphRoute(ICodeGraphExtractor? extractor, string? ambiguous, string? failed)
    {
        Extractor = extractor;
        Ambiguous = ambiguous;
        Failed = failed;
    }

    /// <summary>The implementation; <see langword="null"/> where the file is ambiguous, or a content test failed.</summary>
    public ICodeGraphExtractor? Extractor { get; }

    /// <summary>Why no implementation reads it, its claimants' tests saying none or several; <see langword="null"/> otherwise.</summary>
    public string? Ambiguous { get; }

    /// <summary>Why no implementation reads it, a content test of a claimant having failed on it; <see langword="null"/> otherwise.</summary>
    public string? Failed { get; }

    /// <summary>The file goes to <paramref name="extractor"/>.</summary>
    internal static CodeGraphRoute To(ICodeGraphExtractor extractor)
    {
        ArgumentNullException.ThrowIfNull(extractor);
        return new(extractor, null, null);
    }

    /// <summary>No implementation reads the file, its claimants' tests saying none or several, as <paramref name="why"/> says.</summary>
    internal static CodeGraphRoute AmbiguousFor(string why)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(why);
        return new(null, why, null);
    }

    /// <summary>No implementation reads the file, a content test having failed on it, as <paramref name="why"/> says.</summary>
    internal static CodeGraphRoute FailedFor(string why)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(why);
        return new(null, null, why);
    }
}
