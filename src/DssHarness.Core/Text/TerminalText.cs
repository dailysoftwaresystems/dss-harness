using System.Text.RegularExpressions;

namespace DssHarness.Core.Text;

/// <summary>
/// A line as a terminal shows it: the escape sequences a tool writes for a terminal - its colours, its cursor moves, the
/// titles and links it sets - taken off, so what is read is the text a person reads. The one owner of that rule: every
/// reader of a tool's output that matches its words reads them through it.
/// </summary>
public static class TerminalText
{
    /// <summary>A control sequence (<c>ESC [</c> ... final byte), an operating-system command (<c>ESC ]</c> ... BEL or ST), or a two-byte escape.</summary>
    private static readonly Regex Escape = LinearRegex.Compile(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)|[@-Z\\-_])");

    /// <summary><paramref name="line"/> with every escape sequence a terminal reads taken off; the line itself where it holds none.</summary>
    /// <param name="line">The line as the tool wrote it.</param>
    public static string Plain(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        return line.Contains('\x1B', StringComparison.Ordinal) ? Escape.Replace(line, string.Empty) : line;
    }
}
