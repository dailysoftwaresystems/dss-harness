using System.Collections.Concurrent;
using DssHarness.Core.Hosts;
using DssHarness.Core.Repository;

namespace DssHarness.Core.Secrets;

/// <summary>What one host's item declares that is no credential, as far as it could be read.</summary>
public sealed record ItemFacts
{
    /// <summary>The item's .env, as the repository spells it; <see langword="null"/> where nothing was looked for.</summary>
    public string? File { get; init; }

    /// <summary>Whether this machine holds the item's .env at all.</summary>
    public bool Present { get; init; }

    /// <summary>
    /// Where the host keeps its copy of the main checkout for this machine, as the item sets it over config.json's
    /// <c>repositoryPath</c>; <see langword="null"/> where it sets none.
    /// </summary>
    public string? RepositoryPath { get; init; }

    /// <summary>The distribution a WSL item names, spelt as WSL lists it; <see langword="null"/> for any other host, or one not read.</summary>
    public string? Distribution { get; init; }

    /// <summary>Why the item this machine holds cannot be used, naming the file and the key at fault; <see langword="null"/> where it can.</summary>
    public string? Problem { get; init; }
}

/// <summary>
/// What the hosts' items declare that is no credential, for one command: read from this machine's items, each once, as it is
/// first asked (<see cref="IHostSecretsStore.ReadFacts"/>); or, on a host running legs another machine sent it - whose copy
/// holds no item - the distributions that machine told it beside the legs.
/// </summary>
/// <remarks>
/// Held by the command's context (<see cref="HarnessContext.HostItems"/>), so every reader of a host's copy - placing a leg,
/// syncing, listing, cleaning, removing - asks one answer, and none of them reads config.json's <c>repositoryPath</c> past an
/// item that sets its own. Never a credential: a superuser's is read where it is used, and nowhere else.
/// </remarks>
public sealed class HostItems
{
    private readonly Func<HostId, ItemFacts> _read;
    private readonly ConcurrentDictionary<HostId, ItemFacts> _known = new();

    private HostItems(Func<HostId, ItemFacts> read) => _read = read;

    /// <summary>
    /// What a context made from a configuration alone knows of the items: nothing, so every host's copy is where config.json
    /// says and no WSL item's distribution is known.
    /// </summary>
    public static HostItems None { get; } = new(_ => new ItemFacts());

    /// <summary>The items this machine holds for <paramref name="layout"/>'s repository, each read as it is first asked.</summary>
    /// <param name="secrets">Reads an item.</param>
    /// <param name="layout">Where the repository's harness state lives.</param>
    public static HostItems Reading(IHostSecretsStore secrets, HarnessLayout layout)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(layout);

        return new(host => secrets.ReadFacts(layout, host));
    }

    /// <summary>
    /// What the machine that sent legs here told of its WSL items - each one's distribution, by the item's name - which a
    /// host's copy holds no item to read for itself.
    /// </summary>
    /// <param name="distributions">Each WSL item that machine read, and the distribution it names.</param>
    public static HostItems Told(IReadOnlyDictionary<string, string> distributions)
    {
        ArgumentNullException.ThrowIfNull(distributions);

        return new(host => host.Kind == HostKind.Wsl && distributions.TryGetValue(host.Name, out var distribution)
            ? new ItemFacts { Distribution = distribution }
            : new ItemFacts());
    }

    /// <summary>What <paramref name="host"/>'s item declares, read once.</summary>
    /// <param name="host">The host.</param>
    public ItemFacts Of(HostId host)
    {
        ArgumentNullException.ThrowIfNull(host);

        return _known.GetOrAdd(host, _read);
    }

    /// <summary>
    /// Each of <paramref name="items"/>, by its name, with the distribution it names here, or <see langword="null"/> where
    /// none could be read: what a run line's <c>{distribution.&lt;item&gt;}</c> is filled with, and what a leg sent to a
    /// host carries there.
    /// </summary>
    /// <param name="items">The WSL items the configuration declares.</param>
    public IReadOnlyDictionary<string, string?> Distributions(IEnumerable<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var distributions = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            distributions[item] = Of(HostId.Wsl(item)).Distribution is { Length: > 0 } distribution ? distribution : null;
        }

        return distributions;
    }
}
