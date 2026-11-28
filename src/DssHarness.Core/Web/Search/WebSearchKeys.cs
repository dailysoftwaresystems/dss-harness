using DssHarness.Core.Ci;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Secrets;

namespace DssHarness.Core.Web.Search;

/// <summary>
/// The keys of the web search's sources, as a step that searches them is given them: a variable of the agents' <c>.env</c>
/// slot, read by the slot's one reader, or the token of the machine's <c>gh</c> login for the source's host, held by its one
/// owner (<see cref="GhLogins"/>) - read through the one runner of <c>gh</c> once per engine session, never its default
/// host's, which <c>GH_HOST</c> or a lone login makes another. Optional, never required: where a key is not there, the
/// source is asked without it and the step's record says why. Each value read is a secret, which the step's mask must hold
/// in every form it is sent in (<see cref="WebSearchCredential.Forms"/>).
/// </summary>
/// <param name="slot">Reads the agents' key slot.</param>
/// <param name="gh">Runs <c>gh</c>.</param>
/// <param name="clock">Says when a login's expiry has come; the system's where none is given.</param>
public sealed class WebSearchKeys(AgentKeyStore slot, GhProcess gh, TimeProvider? clock = null)
{
    private readonly AgentKeyStore _slot = slot;
    private readonly GhProcess _gh = gh;
    private readonly TimeProvider? _clock = clock;

    /// <summary>The machine's gh logins as the calling work holds them: its engine session's, else a holder of the caller's own (<see cref="GhLogins.For"/>).</summary>
    public GhLogins Logins() => GhLogins.For(_gh, _clock);

    /// <summary>Each of <paramref name="sources"/>' keys that names one, by the source's name: what is sent, or why there is none.</summary>
    /// <param name="layout">The repository: the slot is its main checkout's, and <c>gh</c> runs there.</param>
    /// <param name="appointed">Every name the configuration appoints a value of the slot for.</param>
    /// <param name="sources">The step's sources.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    /// <param name="held">The gh logins the keys are read of, which the step's search renews by (<see cref="WebSearchRenewal"/>); the calling work's (<see cref="Logins"/>) where none is given.</param>
    public async Task<IReadOnlyDictionary<string, WebSearchKey>> ReadAsync(HarnessLayout layout, IReadOnlyList<AppointedKey> appointed, IReadOnlyList<WebSearchSource> sources, CancellationToken cancellationToken, GhLogins? held = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(appointed);
        ArgumentNullException.ThrowIfNull(sources);

        var keys = new Dictionary<string, WebSearchKey>(StringComparer.Ordinal);
        var logins = new Dictionary<string, WebSearchKey>(StringComparer.OrdinalIgnoreCase);
        held ??= Logins();

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
                        logins[host] = login = KeyOf(await held.ReadAsync(layout.MainCheckoutRoot, host, cancellationToken).ConfigureAwait(false));
                    }

                    keys[source.Name] = login;
                    break;
            }
        }

        return keys;
    }

    /// <summary>
    /// The mask a step - or a side-task - given <paramref name="sources"/>' keys runs under: every form each key read is sent
    /// in (<see cref="WebSearchCredential.Forms"/>), each held as a secret of its own, so no record of the run - its
    /// stream, its result, its notes - writes one. The search itself refuses a mask that does not hold them, and this is
    /// the one builder of the mask that does. Null where no source was given a key.
    /// </summary>
    /// <remarks>
    /// Each form is held under a name of its own, never under its own value: the mask matches values exactly, and two
    /// forms that differ only by case are two secrets, neither of which may be dropped for the other.
    /// </remarks>
    /// <param name="sources">The sources the run was given.</param>
    /// <param name="read">Each source's key, as <see cref="ReadAsync"/> read it.</param>
    public static Runners.ActionValues? Mask(IReadOnlyList<WebSearchSource> sources, IReadOnlyDictionary<string, WebSearchKey> read)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(read);

        var forms = sources
            .Where(source => read.TryGetValue(source.Name, out var key) && key.Value is { Length: > 0 })
            .SelectMany(source => source.Credential?.Forms(read[source.Name].Value!) ?? [read[source.Name].Value!])
            .Where(form => form is { Length: > 0 })
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return forms is [_, ..]
            ? new Runners.ActionValues(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                forms.Select((form, at) => (Name: $"web-search-key-{(at + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)}", Form: form)).ToDictionary(pair => pair.Name, pair => pair.Form, StringComparer.OrdinalIgnoreCase))
            : null;
    }

    /// <summary>A gh login as the key a source is given: its token, or why there is none.</summary>
    /// <param name="login">The login.</param>
    public static WebSearchKey KeyOf(GhLogins.Login login)
    {
        ArgumentNullException.ThrowIfNull(login);
        return new WebSearchKey(login.Token, login.Token is null ? login.Missing : null);
    }
}
