namespace DssHarness.Core.Execution;

/// <summary>
/// Masks the secrets a phase carries in each line its child prints, before anything keeps or shows the line.
/// </summary>
/// <remarks>
/// Supplied by the caller that handed the child its secrets, the only one that knows what to look for. Two questions, asked
/// of one owner so that no caller can answer one and forget the other: what a line reads as once masked, and where a line
/// too long to keep whole can be cut without parting a secret - masked piece by piece, a secret cut in two is masked in
/// neither.
/// </remarks>
public interface ILineMask
{
    /// <summary><paramref name="text"/> with every secret masked.</summary>
    /// <param name="text">A line, or any text about to be kept or shown.</param>
    string Redact(string? text);

    /// <summary>
    /// Where <paramref name="text"/> - the start of a line too long to keep whole, whose rest is still to come - is cut so
    /// that no secret is parted: every secret it holds, and every one its end could be the beginning of, either ends before
    /// the cut or begins at or after it. Zero where no cut can promise that.
    /// </summary>
    /// <param name="text">The start of the line.</param>
    int Cut(string text);
}

/// <summary>What a mask says of a text beside masking it.</summary>
public static class LineMasks
{
    /// <summary>
    /// Whether <paramref name="text"/> holds a secret <paramref name="mask"/> masks: whether masking it would change it.
    /// The one test of it, which an argument list about to be started and what an agent's tool would send are held to.
    /// </summary>
    /// <param name="mask">The mask.</param>
    /// <param name="text">The text.</param>
    public static bool Holds(this ILineMask mask, string text)
    {
        ArgumentNullException.ThrowIfNull(mask);
        ArgumentNullException.ThrowIfNull(text);

        return !string.Equals(mask.Redact(text), text, StringComparison.Ordinal);
    }
}
