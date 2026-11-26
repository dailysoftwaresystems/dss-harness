using DssHarness.Core.Ci;
using DssHarness.Core.Execution;
using DssHarness.Core.Repository;

namespace DssHarness.Core.Web.Search;

/// <summary>
/// What a step's search does with the machine's gh login while it runs, over the logins' one owner
/// (<see cref="GhLogins"/>): each request of a source the login signs is sent the token held now - one read since the step
/// began, by another step of the session or for an expiry that has come, in place of the step's (<see cref="CurrentAsync"/>);
/// a request the host refused is sent once more with the token read again (<see cref="RenewedAsync"/>), never twice; and the
/// expiry a host tells of the token it was sent is kept beside it (<see cref="Answered"/>). Every token the step comes to
/// send is held by its mask before it is sent (<see cref="GrowingMask"/>), so no record of the step writes one.
/// </summary>
/// <param name="logins">The machine's gh logins, as the step's keys were read of them.</param>
/// <param name="layout">The repository, whose main checkout <c>gh</c> runs in.</param>
/// <param name="mask">The step's mask, which every holder of it holds whole.</param>
public sealed class WebSearchRenewal(GhLogins logins, HarnessLayout layout, GrowingMask mask)
{
    private readonly GhLogins _logins = logins ?? throw new ArgumentNullException(nameof(logins));
    private readonly HarnessLayout _layout = layout ?? throw new ArgumentNullException(nameof(layout));
    private readonly GrowingMask _mask = mask ?? throw new ArgumentNullException(nameof(mask));

    /// <summary>
    /// The key <paramref name="source"/> is sent now: <paramref name="key"/>, or - where the source is signed by the gh login
    /// and the login holds another token than the one the step was given - that token, held by the step's mask first.
    /// </summary>
    /// <param name="source">The source asked.</param>
    /// <param name="key">The key the step holds for it; null where it holds none.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    public async Task<WebSearchKey?> CurrentAsync(WebSearchSource source, WebSearchKey? key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.Credential is not { Host: { } host } credential || key?.Value is not { } given)
        {
            return key;
        }

        var login = await _logins.ReadAsync(_layout.MainCheckoutRoot, host, cancellationToken).ConfigureAwait(false);

        return login.Token is { } token && !string.Equals(token, given, StringComparison.Ordinal) ? Held(credential, token) : key;
    }

    /// <summary>
    /// The key a request of <paramref name="source"/> the host refused as unsigned is sent once more: the gh login read
    /// again - held by the step's mask first - where it gives another token than <paramref name="refused"/>'s; null where it
    /// gives none, or the same, and the refusal is answered as it came.
    /// </summary>
    /// <param name="source">The source that refused.</param>
    /// <param name="refused">The key it refused.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    public async Task<WebSearchKey?> RenewedAsync(WebSearchSource source, WebSearchKey refused, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(refused);

        if (source.Credential is not { Host: { } host } credential || refused.Value is not { } token)
        {
            return null;
        }

        var login = await _logins.RenewAsync(_layout.MainCheckoutRoot, host, token, cancellationToken).ConfigureAwait(false);

        return login.Token is { } renewed && !string.Equals(renewed, token, StringComparison.Ordinal) ? Held(credential, renewed) : null;
    }

    /// <summary>Keeps the expiry <paramref name="answer"/> tells of <paramref name="key"/>, the token it was sent, beside it, where the source is signed by the gh login.</summary>
    /// <param name="source">The source that answered.</param>
    /// <param name="key">The key it was sent.</param>
    /// <param name="answer">What it answered.</param>
    public void Answered(WebSearchSource source, WebSearchKey key, WebSearchResponse.Http answer)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(answer);

        if (source.Credential is { Host: { } host } && key.Value is { } token && GhLogins.ExpiryOf(answer.Header(GhLogins.ExpiryHeader)) is { } at)
        {
            _logins.Expires(host, token, at);
        }
    }

    /// <summary><paramref name="token"/> as the key it is sent as, every form of it held by the step's mask first.</summary>
    private WebSearchKey Held(WebSearchCredential credential, string token)
    {
        _mask.Hold(credential.Forms(token));
        return new WebSearchKey(token, null);
    }
}
