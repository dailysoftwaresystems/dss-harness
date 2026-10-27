using DssHarness.Core.Ci;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Secrets;

namespace DssHarness.Core.Web.Search;

/// <summary>
/// The keys of the web search's sources, as a step that searches them is given them: a variable of the agents' <c>.env</c>
/// slot, read by the slot's one reader, or the token of the machine's <c>gh</c> login for the source's host, read through the
/// one runner of <c>gh</c> - never its default host's, which <c>GH_HOST</c> or a lone login makes another. Optional, never required: where a key is not there, the source is asked without it and the step's record
/// says why. Each value read is a secret, which the step's mask must hold in every form it is sent in
/// (<see cref="WebSearchCredential.Forms"/>).
/// </summary>
/// <param name="slot">Reads the agents' key slot.</param>
/// <param name="gh">Runs <c>gh</c>.</param>
public sealed class WebSearchKeys(AgentKeyStore slot, GhProcess gh)
{
    /// <summary>How long the machine's <c>gh</c> login is waited for.</summary>
    public static readonly TimeSpan GhLimit = TimeSpan.FromSeconds(30);

    private readonly AgentKeyStore _slot = slot;
    private readonly GhProcess _gh = gh;

    /// <summary>Each of <paramref name="sources"/>' keys that names one, by the source's name: what is sent, or why there is none.</summary>
    /// <param name="layout">The repository: the slot is its main checkout's, and <c>gh</c> runs there.</param>
    /// <param name="appointed">Every name the configuration appoints a value of the slot for.</param>
    /// <param name="sources">The step's sources.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    public async Task<IReadOnlyDictionary<string, WebSearchKey>> ReadAsync(HarnessLayout layout, IReadOnlyList<AppointedKey> appointed, IReadOnlyList<WebSearchSource> sources, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(appointed);
        ArgumentNullException.ThrowIfNull(sources);

        var keys = new Dictionary<string, WebSearchKey>(StringComparer.Ordinal);
        var logins = new Dictionary<string, WebSearchKey>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in sources)
        {
            switch (source.Credential)
            {
                case { From: "env", Name: { } name }:
                    var answer = _slot.Value(layout, appointed, name);
                    keys[source.Name] = answer.Value is { } value ? new WebSearchKey(value, null) : new WebSearchKey(null, answer.Problem);
                    break;

                case { From: "gh", Host: { } host }:
                    // One login for every source of a host, read once.
                    if (!logins.TryGetValue(host, out var login))
                    {
                        logins[host] = login = await LoginAsync(layout, host, cancellationToken).ConfigureAwait(false);
                    }

                    keys[source.Name] = login;
                    break;
            }
        }

        return keys;
    }

    private async Task<WebSearchKey> LoginAsync(HarnessLayout layout, string host, CancellationToken cancellationToken)
    {
        try
        {
            var token = (await _gh.RunAsync(layout.MainCheckoutRoot, ["auth", "token", "--hostname", host], "so the source is asked without the machine's gh login", GhLimit, cancellationToken).ConfigureAwait(false)).Trim();

            return token.Length > 0 ? new WebSearchKey(token, null) : new WebSearchKey(null, $"gh answered no token: the machine's gh login for {host} is signed out");
        }
        catch (HarnessException ex)
        {
            return new WebSearchKey(null, ex.Message);
        }
    }
}
