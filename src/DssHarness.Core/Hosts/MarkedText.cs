using System.Security.Cryptography;

namespace DssHarness.Core.Hosts;

/// <summary>
/// Reads a stream a host wrote, past the line that marks where what was asked of it begins: whatever came
/// before is the host's - its shell's startup files, its profile - and not the answer.
/// </summary>
public static class MarkedText
{
    /// <summary>A new mark: random, so no output of a command holds it by chance.</summary>
    public static string NewMark() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>Whether <paramref name="line"/> carries <paramref name="mark"/>.</summary>
    /// <remarks>
    /// Matched as the end of the line rather than the whole of it. A startup file whose last write has no
    /// trailing newline - a prompt, an escape sequence, an <c>echo -n</c> - glues its bytes onto the first
    /// line written after it, which is the mark. Held to the whole line, such a host would never be read past
    /// it at all. Trailing blanks are cmd's: its echo keeps the space before a redirection.
    /// </remarks>
    public static bool IsMarkLine(string line, string mark)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentException.ThrowIfNullOrWhiteSpace(mark);

        return line.TrimEnd().EndsWith(mark, StringComparison.Ordinal);
    }

    /// <summary>
    /// <paramref name="text"/> after the last line <see cref="IsMarkLine"/> holds to carry <paramref name="mark"/>,
    /// or the whole of it where no line does: a host that never reached the point the mark is written at has
    /// nothing else to show, so there the whole of it is the answer.
    /// </summary>
    /// <remarks>
    /// Read line by line, by the rule <see cref="MarkedLines"/> reads a stream with, and never by where the mark's
    /// text happens to occur: PowerShell's own error for a command it could not run quotes the command line, the
    /// mark in it twice, and cut there the error lost the very words that said what went wrong.
    /// </remarks>
    public static string Since(string text, string mark)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(mark);

        var start = 0;
        var after = -1;

        while (start < text.Length)
        {
            var end = text.IndexOf('\n', start);
            var line = end < 0 ? text[start..] : text[start..end];

            if (IsMarkLine(line, mark))
            {
                after = end < 0 ? text.Length : end + 1;
            }

            if (end < 0)
            {
                break;
            }

            start = end + 1;
        }

        return after < 0 ? text : text[after..];
    }
}

/// <summary>
/// Hands on the lines of a stream that come after the one carrying a mark, as they arrive, and drops the ones before
/// it; or, where the mark never came, hands those on once the stream has ended, as <see cref="MarkedText.Since"/> keeps them.
/// </summary>
/// <remarks>
/// What precedes the mark on a stream that carries one is the host's - its shell's startup files - and what precedes it on
/// one that never does is everything there is: ssh's own words about a connection that never began a session, which the
/// reader is owed. Held rather than dropped until the mark says which, and handed on at the end, which for ssh comes the
/// moment it has said why it could not connect.
/// </remarks>
/// <param name="mark">The mark.</param>
/// <param name="receive">Receives each line handed on.</param>
public sealed class MarkedLines(string mark, Action<string> receive)
{
    private readonly string _mark = mark;
    private readonly Action<string> _receive = receive;
    private List<string>? _held = [];

    /// <summary>A gate on <paramref name="receive"/>, or <see langword="null"/> where there is none to hand lines to.</summary>
    public static MarkedLines? For(string mark, Action<string>? receive)
        => receive is null ? null : new MarkedLines(mark, receive);

    /// <summary>Takes one line the stream carried.</summary>
    public void Line(string line)
    {
        if (_held is null)
        {
            _receive(line);
        }
        else if (MarkedText.IsMarkLine(line, _mark))
        {
            _held = null;
        }
        else
        {
            _held.Add(line);
        }
    }

    /// <summary>Hands on the lines held, where the mark never came: the stream has ended.</summary>
    public void Ended()
    {
        if (_held is not { } held)
        {
            return;
        }

        _held = null;

        foreach (var line in held)
        {
            _receive(line);
        }
    }
}
