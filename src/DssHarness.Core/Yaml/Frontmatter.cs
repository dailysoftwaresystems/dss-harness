namespace DssHarness.Core.Yaml;

/// <summary>
/// A markdown file that opens with YAML frontmatter - an agent definition, a skill's <c>SKILL.md</c> - split where its
/// frontmatter closes: the one reader of where the keys end and the body begins, which every parser of such a file and
/// every check of a body's lines asks, so that no two of them disagree on which line is the body's first.
/// </summary>
public static class Frontmatter
{
    /// <summary>
    /// <paramref name="text"/> split at the close of its frontmatter: a <c>---</c> line first, the keys, and the next
    /// <c>---</c> line - which may end the file, the body then empty rather than past the file's end.
    /// </summary>
    /// <param name="text">The file's whole text.</param>
    public static FrontmatterSplit Of(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var opening = text.StartsWith("---\n", StringComparison.Ordinal) || text.StartsWith("---\r\n", StringComparison.Ordinal)
            ? text.IndexOf('\n') + 1
            : 0;

        if (opening == 0)
        {
            return new FrontmatterSplit(Keys: null, Body: null, Opened: false, BodyLine: 0);
        }

        var at = opening;
        var line = 2;

        while (at < text.Length)
        {
            var end = text.IndexOf('\n', at);

            if (text.AsSpan(at, (end < 0 ? text.Length : end) - at).TrimEnd('\r').SequenceEqual("---"))
            {
                return new FrontmatterSplit(text[opening..at], text[(end < 0 ? text.Length : end + 1)..], Opened: true, BodyLine: line + 1);
            }

            if (end < 0)
            {
                break;
            }

            at = end + 1;
            line++;
        }

        return new FrontmatterSplit(Keys: null, Body: null, Opened: true, BodyLine: 0);
    }
}

/// <summary>A file split at the close of its frontmatter (<see cref="Frontmatter.Of"/>).</summary>
/// <param name="Keys">The frontmatter's text between its two <c>---</c> lines; null where the file opens with none or never closes it.</param>
/// <param name="Body">Everything after the closing <c>---</c> line; null with <paramref name="Keys"/>.</param>
/// <param name="Opened">Whether the file's first line is <c>---</c>, which opens a frontmatter, closed or not.</param>
/// <param name="BodyLine">The line of the file the body starts on, counted from 1; naught with <paramref name="Keys"/> null.</param>
public sealed record FrontmatterSplit(string? Keys, string? Body, bool Opened, int BodyLine)
{
    /// <summary>Whether the file holds a frontmatter that closes.</summary>
    public bool Closed => Keys is not null;
}
