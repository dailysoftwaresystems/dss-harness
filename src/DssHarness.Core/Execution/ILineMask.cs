using System.Text;
using AngleSharp.Html;

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

    /// <summary>How many times a text's escapes are undone at most (<see cref="HoldsUnescaped"/>), so no text costs more passes.</summary>
    public const int DeepestReading = 8;

    /// <summary>The most characters a character reference holds between its <c>&amp;</c> and its <c>;</c>: <c>&amp;CounterClockwiseContourIntegral;</c>'s.</summary>
    private const int MostReference = 32;

    /// <summary>The digits of a numeric character reference, a hex one's among them.</summary>
    private static readonly System.Buffers.SearchValues<char> HexDigits = System.Buffers.SearchValues.Create("0123456789ABCDEFabcdef");

    /// <summary>What a character reference holds between its <c>&amp;</c> and its <c>;</c>.</summary>
    private static readonly System.Buffers.SearchValues<char> ReferenceCharacters = System.Buffers.SearchValues.Create("#0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");

    /// <summary>
    /// Whether <paramref name="text"/> holds a secret <paramref name="mask"/> masks as written or in any reading of it with
    /// its escapes undone - percent-escapes, then HTML's character references - once more each time, at most
    /// <see cref="DeepestReading"/> times: an address carried in another's parameter is escaped once more for each address
    /// that carries it, and a page writes a '+' as <c>&amp;#x2B;</c>, past which a secret no longer reads as itself. What
    /// an address a tool of the web would ask is held to.
    /// </summary>
    /// <param name="mask">The mask.</param>
    /// <param name="text">The text, an address most often.</param>
    public static bool HoldsUnescaped(this ILineMask mask, string text)
    {
        ArgumentNullException.ThrowIfNull(mask);
        ArgumentNullException.ThrowIfNull(text);

        return Readings(text).Any(mask.Holds);
    }

    /// <summary>
    /// <paramref name="text"/> as a record, an answer or a reader may take it: masked as written, and where a secret still
    /// hides behind its escapes (<see cref="HoldsUnescaped"/>), its fullest reading that holds one masked - the reading
    /// every escape of it is undone in. A text that holds its secrets plain alone keeps its form, a JSON answer its JSON.
    /// </summary>
    /// <param name="mask">The mask.</param>
    /// <param name="text">The text, an address or an answer.</param>
    public static string RedactUnescaped(this ILineMask mask, string text)
    {
        ArgumentNullException.ThrowIfNull(mask);
        ArgumentNullException.ThrowIfNull(text);

        var masked = mask.Redact(text);

        return Readings(masked).LastOrDefault(mask.Holds) is { } holding ? mask.Redact(holding) : masked;
    }

    /// <summary>
    /// <paramref name="head"/> - the start of a text cut short - as <see cref="RedactUnescaped"/> masks a text, cut first
    /// before a secret its end could be the start of in any reading of it, the fullest first: that reading - an escape the
    /// cut left unfinished at its end dropped before each is read, so none is read as another character, and last the head
    /// as it stands, whose end may be a secret's start right after an <c>&amp;</c> - cut by the mask's own cut
    /// (<see cref="ILineMask.Cut"/>). No half of a secret is told, nor of one behind escapes the cut parted - a page's
    /// <c>&amp;#x2B;</c> cut after its <c>&amp;#x2</c>.
    /// </summary>
    /// <param name="mask">The mask.</param>
    /// <param name="head">The start of the text.</param>
    public static string RedactHead(this ILineMask mask, string head)
    {
        ArgumentNullException.ThrowIfNull(mask);
        ArgumentNullException.ThrowIfNull(head);

        foreach (var reading in Readings(head, finished: true).Reverse().Append(head))
        {
            if (mask.Cut(reading) is var cut && cut < reading.Length)
            {
                return mask.RedactUnescaped(Output.TextCut.Head(reading, cut));
            }
        }

        return mask.RedactUnescaped(head);
    }

    /// <summary>
    /// <paramref name="reading"/> without the escape its end leaves unfinished, where it does: a <c>%</c> and at most one
    /// hex digit, or a <c>&amp;</c> and what could follow it before its <c>;</c> - a name, or a number of any length. What a text cut short is cut to before a
    /// reader decodes it, so no reader makes another character of half an escape - a page's <c>&amp;#x2</c> read as U+0002
    /// - nor of the secret's start before it anything but itself.
    /// </summary>
    /// <param name="reading">The text, or a reading of it.</param>
    internal static string Finished(string reading)
    {
        var at = reading.LastIndexOfAny(['%', '&']);

        if (at < 0)
        {
            return reading;
        }

        var rest = reading.AsSpan(at + 1);
        var number = rest.StartsWith("#") ? rest[1..] is ['x' or 'X', .. var hex] ? hex : rest[1..] : default;
        var unfinished = reading[at] == '%' ? rest.Length <= 1 && (rest.IsEmpty || char.IsAsciiHexDigit(rest[0]))
            : rest.StartsWith("#") ? !number.ContainsAnyExcept(HexDigits)
            : rest.Length <= MostReference && !rest.ContainsAnyExcept(ReferenceCharacters);

        return unfinished ? reading[..at] : reading;
    }

    /// <summary>
    /// <paramref name="text"/>, then each reading of it with its escapes undone once more - percent-escapes, then HTML's
    /// character references as a page's reader undoes them (<see cref="HtmlDecoded"/>) - until one undoes none or
    /// <see cref="DeepestReading"/> have; where it is <paramref name="finished"/>, an escape its end leaves unfinished
    /// dropped before each kind of escape is undone (<see cref="Finished"/>) - half of an HTML reference a percent-escape
    /// wrote among them.
    /// </summary>
    private static IEnumerable<string> Readings(string text, bool finished = false)
    {
        // Each escape undone at a time - percent's, then HTML's - with what its end leaves unfinished dropped before HTML's,
        // which would read half of one as another character; the next reading's turn drops what HTML's leaves.
        string Read(string read) => finished ? Finished(read) : read;

        var reading = Read(text);

        for (var depth = 0; ; depth++)
        {
            yield return reading;

            // Each escape undone shortens the reading, so the readings end - and past the deepest no text is read further,
            // so a text that unescapes into itself a thousand times costs no thousand passes.
            var unescaped = depth == DeepestReading ? reading : HtmlDecoded(Read(Uri.UnescapeDataString(reading)));

            if (string.Equals(unescaped, reading, StringComparison.Ordinal))
            {
                yield break;
            }

            reading = unescaped;
        }
    }

    /// <summary>
    /// <paramref name="text"/> with each of HTML's character references undone as a page's reader undoes one in its text
    /// (the HTML standard's tokenizer, which <see cref="HtmlEntityProvider"/> holds the names of): a name the standard
    /// holds, the longest that matches - its legacy ones with no <c>;</c> among them - and a number, decimal or hex, with
    /// its <c>;</c> or without it. What a reader decodes, no secret hides behind.
    /// </summary>
    private static string HtmlDecoded(string text)
    {
        var at = text.IndexOf('&', StringComparison.Ordinal);

        if (at < 0)
        {
            return text;
        }

        var decoded = new StringBuilder(text.Length);
        var from = 0;

        for (; at >= 0; at = text.IndexOf('&', from))
        {
            decoded.Append(text, from, at - from);

            var (said, length) = Reference(text, at + 1);
            decoded.Append(said ?? "&");
            from = at + 1 + length;
        }

        return decoded.Append(text, from, text.Length - from).ToString();
    }

    /// <summary>
    /// The character reference that begins at <paramref name="start"/>, just past its <c>&amp;</c>: what it says, and how
    /// many characters past the <c>&amp;</c> it takes; nothing, taking none, where none begins there.
    /// </summary>
    private static (string? Said, int Length) Reference(string text, int start)
    {
        if (start < text.Length && text[start] == '#')
        {
            var hex = start + 1 < text.Length && text[start + 1] is 'x' or 'X';
            var digits = start + (hex ? 2 : 1);
            var end = digits;
            var number = 0u;

            // As a page's reader reads one: its digits all taken, however many, and its number what is left of them in 32 bits.
            for (; end < text.Length && (hex ? char.IsAsciiHexDigit(text[end]) : char.IsAsciiDigit(text[end])); end++)
            {
                number = unchecked((number * (hex ? 16u : 10u)) + (uint)(text[end] <= '9' ? text[end] - '0' : (text[end] | 0x20) - 'a' + 10));
            }

            return end == digits ? (null, 0) : (Character(number), end - start + (end < text.Length && text[end] == ';' ? 1 : 0));
        }

        var name = start;

        while (name < text.Length && name - start < MostReference && char.IsAsciiLetterOrDigit(text[name]))
        {
            name++;
        }

        for (var length = name - start + (name < text.Length && text[name] == ';' ? 1 : 0); length > 0; length--)
        {
            if (HtmlEntityProvider.Resolver.GetSymbol(text.Substring(start, length)) is { } symbol)
            {
                return (symbol, length);
            }
        }

        return (null, 0);
    }

    /// <summary>What a numeric reference to <paramref name="number"/> reads as: a number no character has is U+FFFD, and one of the C1 controls the character a page in windows-1252 meant.</summary>
    private static string Character(uint number)
        => number <= 0x10FFFF && HtmlEntityProvider.IsInCharacterTable((int)number) && HtmlEntityProvider.GetSymbolFromTable((int)number) is { } meant ? meant
            : number is 0 or > 0x10FFFF or (>= 0xD800 and <= 0xDFFF) ? "\uFFFD"
            : char.ConvertFromUtf32((int)number);
}
