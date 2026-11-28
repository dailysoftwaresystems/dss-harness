using DssHarness.Core.Runners;

namespace DssHarness.Core.Execution;

/// <summary>
/// Every mask one engine session's records were kept through, held in one place for what the session's command leaves at
/// its end - its output logs (<see cref="Orchestration.Flow.FlowOutputLogs"/>), which so hold nothing a record of the
/// session was masked of: the secrets the configuration appoints that the key slot answers, as the session began; each mask
/// the one builder of a launch's mask made while it ran (<see cref="Orchestration.Flow.ReasonLaunch.Mask"/>), the forms
/// every web-search key a step was given is sent in among them; and each mask that came to hold the machine's gh login read
/// again while a step ran (<see cref="GrowingMask"/>), read as it holds its tokens when it is asked.
/// </summary>
/// <remarks>
/// Held as each was built, never built again at the end: a configuration edited or a key slot changed since the session
/// began masks nothing its records were masked of, and leaves out nothing they were.
/// </remarks>
public sealed class SessionMasks
{
    private readonly Lock _gate = new();
    private readonly List<ActionValues> _built = [];
    private readonly List<GrowingMask> _growing = [];
    private string? _unbuilt;

    /// <summary>The masks the calling work's engine session holds, made at its first asking; null outside a session.</summary>
    public static SessionMasks? OfSession() => EngineSession.Of(() => new SessionMasks());

    /// <summary>Holds <paramref name="mask"/>, which a record of the session is kept through; nothing where it is null, a mask of no secret.</summary>
    /// <param name="mask">The mask.</param>
    public void Hold(ActionValues? mask)
    {
        if (mask is null)
        {
            return;
        }

        lock (_gate)
        {
            _built.Add(mask);
        }
    }

    /// <summary>Holds <paramref name="mask"/>, which comes to hold each token a step reads again before it is sent, as it holds them whenever it is asked.</summary>
    /// <param name="mask">The mask.</param>
    public void Hold(GrowingMask mask)
    {
        ArgumentNullException.ThrowIfNull(mask);

        lock (_gate)
        {
            _growing.Add(mask);
        }
    }

    /// <summary>
    /// Says why the session could not build the mask of the secrets its configuration appoints as it began - its providers
    /// do not resolve, or the key slot could not be read - so no mask stands for them: what the session leaves at its end
    /// leaves out what only that mask would have masked.
    /// </summary>
    /// <param name="why">Why, as a clause.</param>
    public void Unbuilt(string why)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(why);

        lock (_gate)
        {
            _unbuilt ??= why;
        }
    }

    /// <summary>
    /// Every secret held, as one mask - null where the session held none - or why the session could build none of the
    /// secrets its configuration appoints.
    /// </summary>
    public (ActionValues? Mask, string? Unbuilt) Current()
    {
        lock (_gate)
        {
            var masks = _built.Concat(_growing.Select(growing => growing.Held)).ToList();

            return (masks is [] ? null : masks.Skip(1).Aggregate(masks[0], (whole, mask) => whole.With(mask)), _unbuilt);
        }
    }
}
