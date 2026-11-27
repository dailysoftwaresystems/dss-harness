using DssHarness.Core.Runners;

namespace DssHarness.Core.Execution;

/// <summary>
/// A step's mask that comes to hold a secret read while the step runs - the machine's gh login read again for a source that
/// refused or would soon refuse the one the step began with (<see cref="Ci.GhLogins"/>) - before that secret is sent
/// anywhere: every line the step keeps from then on is masked of it as of every secret it began with, and every holder of
/// the step's mask holds this one, never a copy.
/// </summary>
/// <param name="began">The secrets the step began with.</param>
public sealed class GrowingMask(ActionValues began) : ILineMask
{
    private ActionValues _held = began ?? throw new ArgumentNullException(nameof(began));
    private int _added;

    /// <summary>Every secret the mask holds now - those it began with, and each held since - as one mask.</summary>
    public ActionValues Held => Volatile.Read(ref _held);

    /// <inheritdoc/>
    public string Redact(string? text) => Volatile.Read(ref _held).Redact(text);

    /// <inheritdoc/>
    public int Cut(string text) => Volatile.Read(ref _held).Cut(text);

    /// <summary>
    /// Holds each of <paramref name="secrets"/> from now on, beside every secret held already; a secret held already is
    /// held still, once.
    /// </summary>
    /// <param name="secrets">The secrets, each a form a value is sent or echoed in.</param>
    public void Hold(IEnumerable<string> secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);

        var named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var secret in secrets.Where(secret => secret is { Length: > 0 }))
        {
            named[$"held-{Interlocked.Increment(ref _added).ToString(System.Globalization.CultureInfo.InvariantCulture)}"] = secret;
        }

        if (named.Count == 0)
        {
            return;
        }

        var adding = new ActionValues(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), named);

        while (true)
        {
            var held = Volatile.Read(ref _held);

            if (ReferenceEquals(Interlocked.CompareExchange(ref _held, held.With(adding), held), held))
            {
                return;
            }
        }
    }
}
