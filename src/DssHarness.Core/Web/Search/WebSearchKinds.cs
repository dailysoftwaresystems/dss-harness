using System.Globalization;
using DssHarness.Core.Configuration;
using DssHarness.Core.Results;

namespace DssHarness.Core.Web.Search;

/// <summary>
/// The one owner of the implementations of <see cref="IWebSearchKind"/>: which kinds a source may name, the keys each
/// takes, and what a source's keys are refused for all come from it.
/// </summary>
/// <remarks>
/// Built from every implementation the container holds, which <see cref="Implementations"/> - one scan of the core - gives
/// it, so a new kind is registered by being written. Two kinds of one name are refused when it is built, never decided by
/// an order nobody reads in the two classes; so is a kind whose keys would shadow a source's own. A test builds it from
/// the real container on every run of the suite, so a clash is a red test in the change that brings it.
/// </remarks>
public sealed class WebSearchKinds
{
    private static WebSearchKinds? _thisBuild;

    /// <summary>Builds the registry of <paramref name="kinds"/>, refusing what would make a source's kind a matter of order.</summary>
    /// <param name="kinds">Every implementation, each once.</param>
    /// <exception cref="HarnessException">
    /// Two share a name; a name is not one a source can write; a version is below 1; a kind takes a key twice, or one of
    /// every source's own; a kind says nothing of itself (<see cref="HarnessExit.InternalError"/>: a defect of this build).
    /// </exception>
    public WebSearchKinds(IEnumerable<IWebSearchKind> kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);

        var all = kinds.OrderBy(kind => kind.Kind, StringComparer.Ordinal).ToList();

        Refuse(all.GroupBy(kind => kind.Kind, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1) is { } twice
            ? $"two implementations of the web search's contract are named '{twice.Key}': {string.Join(", ", twice.Select(kind => kind.GetType().FullName))}; a source names its kind by it, and one implementation answers to it"
            : null);

        foreach (var kind in all)
        {
            var own = kind.Keys.FirstOrDefault(key => WebSearchSource.OwnKeys.Any(source => string.Equals(source.Name, key.Name, StringComparison.OrdinalIgnoreCase)));
            var repeated = kind.Keys.GroupBy(key => key.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);

            Refuse(!WebSearchSource.IsName(kind.Kind) ? $"'{kind.Kind}' ({kind.GetType().FullName}) is not a kind's name: {WebSearchSource.NameRule}"
                : kind.Version < 1 ? $"'{kind.Kind}' is at version {kind.Version.ToString(CultureInfo.InvariantCulture)}; a version is a positive integer, raised whenever what the kind asks or reads changes"
                : own is not null ? $"'{kind.Kind}' takes the key '{own.Name}', which is every source's own"
                : repeated is not null ? $"'{kind.Kind}' takes the key '{repeated.Key}' twice"
                : string.IsNullOrWhiteSpace(kind.Help) ? $"'{kind.Kind}' says nothing of itself; its help is what a person reads of it"
                : null);
        }

        All = all;
    }

    /// <summary>
    /// Every concrete type of the core that implements <see cref="IWebSearchKind"/>, by its full name: the one scan that
    /// finds them (<see cref="Composition.CoreImplementations{T}"/>), which the composition root registers each of and the
    /// registry of this build is made from.
    /// </summary>
    public static IReadOnlyList<Type> Implementations => Composition.CoreImplementations<IWebSearchKind>.Types;

    /// <summary>The registry of this build's own kinds, each made with no argument as the container makes it: for what has no container to ask - help.</summary>
    public static WebSearchKinds OfThisBuild
        => LazyInitializer.EnsureInitialized(ref _thisBuild, () => new(Composition.CoreImplementations<IWebSearchKind>.OfThisBuild));

    /// <summary>Every kind, by its name.</summary>
    public IReadOnlyList<IWebSearchKind> All { get; }

    /// <summary>The kind named <paramref name="kind"/>, in any case; <see langword="null"/> where none is.</summary>
    /// <param name="kind">A kind's name.</param>
    public IWebSearchKind? Find(string? kind)
        => All.FirstOrDefault(candidate => string.Equals(candidate.Kind, kind, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Why <paramref name="source"/> cannot be searched, each problem one line naming its key: no kind, or one no
    /// implementation is; a key its kind requires missing; a key neither every source's nor its kind's; what is wrong
    /// with the values of every source's own keys (<see cref="WebSearchSource.OwnProblems"/>); and what its kind finds in
    /// its keys' values. Empty where it can be.
    /// </summary>
    /// <param name="source">The source.</param>
    public IReadOnlyList<string> Problems(WebSearchSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.Kind is not { } named)
        {
            return [source.Has("kind")
                ? $"'kind' is the name of a kind, one of {Names()}"
                : $"'kind' is missing: a source names its kind, one of {Names()}", .. source.OwnProblems()];
        }

        if (Find(named) is not { } kind)
        {
            return [$"'kind' is '{named}', which is no kind; the kinds are {Names()}", .. source.OwnProblems()];
        }

        var problems = new List<string>(source.OwnProblems());

        problems.AddRange(kind.Keys
            .Where(key => key.Required && !source.Has(key.Name))
            .Select(key => $"'{key.Name}' is missing: a source of kind '{kind.Kind}' gives it - {key.Meaning}"));

        var taken = WebSearchSource.OwnKeys.Concat(kind.Keys).Select(key => key.Name).ToList();

        problems.AddRange(source.Keys
            .Where(key => !taken.Contains(key, StringComparer.OrdinalIgnoreCase))
            .Select(key => $"'{key}' is no key a source of kind '{kind.Kind}' takes; it takes {string.Join(", ", taken)}"));

        problems.AddRange(kind.Problems(source));

        return problems;
    }

    private string Names() => string.Join(", ", All.Select(kind => kind.Kind));

    private static void Refuse(string? problem)
    {
        if (problem is not null)
        {
            throw new HarnessException(HarnessExit.InternalError, $"The web search's kinds do not hold together: {problem}.");
        }
    }
}
