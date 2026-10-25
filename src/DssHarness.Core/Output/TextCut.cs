namespace DssHarness.Core.Output;

/// <summary>
/// A text cut to a number of characters from its start or its end, never inside a surrogate pair: half of one is no
/// character, and reads as nothing, or as an error, to whatever is handed it. The one rule of a cut by characters.
/// </summary>
public static class TextCut
{
    /// <summary>
    /// At most the first <paramref name="length"/> characters of <paramref name="text"/>: one fewer where the last would be
    /// the first half of a pair. The text whole where it is no longer.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="length">The most characters kept.</param>
    public static string Head(string text, int length)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        if (length >= text.Length)
        {
            return text;
        }

        return text[..(length > 0 && char.IsHighSurrogate(text[length - 1]) ? length - 1 : length)];
    }

    /// <summary>
    /// At most the last <paramref name="length"/> characters of <paramref name="text"/>: one fewer where the first would be
    /// the second half of a pair. The text whole where it is no longer.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="length">The most characters kept.</param>
    public static string Tail(string text, int length)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        if (length >= text.Length)
        {
            return text;
        }

        var start = text.Length - length;

        return text[(start < text.Length && char.IsLowSurrogate(text[start]) ? start + 1 : start)..];
    }
}
