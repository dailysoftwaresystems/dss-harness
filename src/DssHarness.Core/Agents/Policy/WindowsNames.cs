using System.Text.RegularExpressions;
using DssHarness.Core.Text;

namespace DssHarness.Core.Agents.Policy;

/// <summary>
/// How Windows reads a name along a path, where its spelling and the name it reaches differ: the one owner the agents'
/// policy reads every path segment through on Windows before any guard compares it. A segment's trailing dots and
/// spaces are dropped (<c>.git.</c>, <c>k </c>), as Windows drops them; an NTFS stream or attribute suffix after its
/// first colon is dropped (<c>.git::$INDEX_ALLOCATION</c>, <c>k::$DATA</c>), for it names the file or directory itself or
/// a stream of it; and a short name (<c>GIT~1</c>, <c>harnes~1</c>), which the system gives as its long one where the path
/// exists, may be any name it could shorten where it does not - one a command may make before the word is read.
/// </summary>
public static class WindowsNames
{
    /// <summary>
    /// <paramref name="path"/> with each segment as Windows reads it: what follows its first colon dropped - an NTFS stream
    /// or attribute (<c>::$DATA</c>, <c>:$I30:$INDEX_ALLOCATION</c>) - but a drive's at its start (<c>C:</c>), and its
    /// trailing dots and spaces dropped; a segment of dots alone (<c>.</c>, <c>..</c>) kept as it is, and one the trim
    /// would leave empty read as the step its dots spell (<c>.. </c> as <c>..</c>). Windows drops the trailing dots and
    /// spaces of a path's last segment and a single dot from any other; reading every segment so reaches each place a
    /// spelling may reach, never one fewer.
    /// </summary>
    /// <param name="path">The path, in either separator.</param>
    public static string Trimmed(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (path.IndexOfAny([':', '.', ' ']) < 0)
        {
            return path;
        }

        var segments = Separators.Split(path);

        for (var at = 0; at < segments.Length; at += 2)
        {
            // A drive at the path's start keeps its colon; what follows it is the segment's name.
            var drive = at == 0 && segments[at].Length >= 2 && char.IsAsciiLetter(segments[at][0]) && segments[at][1] == ':' ? segments[at][..2] : string.Empty;
            var rest = segments[at][drive.Length..];
            var colon = rest.IndexOf(':', StringComparison.Ordinal);
            var named = colon > 0 ? rest[..colon] : rest;
            var trimmed = named.TrimEnd('.', ' ');

            // A segment of dots alone is a relative step; one left empty by the trim is read as the step its dots spell.
            var read = named.Trim('.').Length == 0 ? named
                : trimmed.Length > 0 ? trimmed
                : named.TrimEnd(' ') is "." or ".." ? named.TrimEnd(' ')
                : named;

            segments[at] = drive + read;
        }

        return string.Concat(segments);
    }

    /// <summary>
    /// Whether <paramref name="segment"/> is spelled as a Windows short name - up to six characters, a <c>~</c> and its
    /// number, an extension of up to three - so it may stand for a long name the system gave it.
    /// </summary>
    /// <param name="segment">One segment of a path.</param>
    public static bool IsShortName(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);

        return ShortName.IsMatch(segment);
    }

    /// <summary>
    /// Whether the short name <paramref name="segment"/> may be the one Windows gives <paramref name="name"/>, in any case:
    /// the long name read as the short one is made of it - upper case, its leading dots, its spaces and every dot but its
    /// extension's gone, a character no short name holds read as <c>_</c> - begins with the short name's stem, or with the
    /// stem's first two characters where the four after them are hexadecimal (the hashed form Windows gives a name whose
    /// stem many others share); and the long name's extension begins with the short name's, a long name with none read
    /// only by a short name with none.
    /// </summary>
    /// <param name="segment">A segment spelled as a short name (<see cref="IsShortName"/>).</param>
    /// <param name="name">A long name.</param>
    public static bool MayShorten(string segment, string name)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentNullException.ThrowIfNull(name);

        if (ShortName.Match(segment) is not { Success: true } match)
        {
            return false;
        }

        var stem = match.Groups["stem"].Value.ToUpperInvariant();
        var extension = match.Groups["extension"].Value.ToUpperInvariant();
        var (longStem, longExtension) = Basis(name);

        var stemMatches = longStem.StartsWith(stem, StringComparison.Ordinal)
            || (stem.Length == 6 && longStem.StartsWith(stem[..2], StringComparison.Ordinal) && stem[2..].All(char.IsAsciiHexDigit));

        // A long name with an extension keeps its first characters in its short name; one with none gives it none.
        var extensionMatches = extension.Length == 0 ? longExtension.Length == 0 : longExtension.StartsWith(extension, StringComparison.Ordinal);

        return longStem.Length > 0 && stemMatches && extensionMatches;
    }

    /// <summary>
    /// <paramref name="path"/>, and the path with each of its segments spelled as a short name read as each of
    /// <paramref name="names"/> it may shorten (<see cref="MayShorten"/>), in every combination, however many: the path as
    /// a pattern (<see cref="WordPattern"/>), each such segment a choice of itself and its names, spelled one reading at a
    /// time - the path itself first - and never held all at once.
    /// </summary>
    /// <param name="path">A whole path, after the system has given each short name it knows as its long one.</param>
    /// <param name="names">The names a short name is read as where it may shorten one.</param>
    public static IEnumerable<string> Lengthened(string path, IReadOnlyCollection<string> names)
    {
        var parts = Lengthenings(path, names);

        return parts.Any(part => part.Count > 1)
            ? new WordPattern([.. parts.Select(part => part.Count == 1
                ? new WordPart.Text(part[0], string.Empty)
                : (WordPart)new WordPart.Choice(new WordChoice([.. part.Select(name => WordPattern.Of(name, string.Empty))])))]).Spellings().Select(spelled => spelled.Word)
            : [path];
    }

    /// <summary>
    /// <paramref name="path"/> as the parts its readings (<see cref="Lengthened"/>) are made of, in order: each run of
    /// separators a part of its own, spelled as it is; each segment a part of the names it may be read as - itself first,
    /// then, where it is spelled as a short name, each of <paramref name="names"/> it may shorten (<see cref="MayShorten"/>),
    /// each once in any case. The pattern of the readings, held at the size of the path and its names: a reader that
    /// decides the readings one segment at a time, keeping one of each way they may stand, reads them all without
    /// spelling them out (the agents' policy, <c>AgentPolicy.Readings</c>).
    /// </summary>
    /// <param name="path">A whole path, after the system has given each short name it knows as its long one.</param>
    /// <param name="names">The names a short name is read as where it may shorten one.</param>
    public static IReadOnlyList<IReadOnlyList<string>> Lengthenings(string path, IReadOnlyCollection<string> names)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(names);

        if (!path.Contains('~', StringComparison.Ordinal))
        {
            return [[path]];
        }

        return [.. Separators.Split(path)
            .Select((segment, at) => at % 2 == 0 && IsShortName(segment)
                    && names.Where(name => MayShorten(segment, name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() is { Count: > 0 } longer
                ? (IReadOnlyList<string>)[segment, .. longer]
                : [segment])
            .Where(part => part[0].Length > 0 || part.Count > 1)];
    }

    /// <summary>
    /// The stem and extension Windows makes a short name of <paramref name="name"/> from: upper case, its leading dots
    /// and its spaces gone, every dot but the last gone, the extension its first three characters after that dot, and a
    /// character no short name holds (<c>+ , ; = [ ]</c>) read as <c>_</c>.
    /// </summary>
    private static (string Stem, string Extension) Basis(string name)
    {
        var plain = new string([.. name.ToUpperInvariant().TrimStart('.').Where(character => character != ' ')
            .Select(character => character is '+' or ',' or ';' or '=' or '[' or ']' ? '_' : character)]);
        var dot = plain.LastIndexOf('.');

        return dot < 0
            ? (plain, string.Empty)
            : (plain[..dot].Replace(".", string.Empty, StringComparison.Ordinal), plain[(dot + 1)..] is var extension && extension.Length > 3 ? extension[..3] : extension);
    }

    /// <summary>A run of separators of either kind, kept by a split as the text between a path's segments.</summary>
    private static readonly Regex Separators = LinearRegex.Compile(@"([/\\]+)");

    /// <summary>A short name as Windows spells one: a stem of one to six characters, a <c>~</c> and its number, and an extension of up to three.</summary>
    private static readonly Regex ShortName = LinearRegex.Compile(@"^(?<stem>[^~.\s/\\:*?""<>|]{1,6})~[0-9]{1,6}(\.(?<extension>[^.\s/\\:*?""<>|]{1,3}))?$");
}
