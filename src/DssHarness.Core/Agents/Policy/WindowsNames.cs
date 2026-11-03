using System.Text.RegularExpressions;

namespace DssHarness.Core.Agents.Policy;

/// <summary>
/// How Windows reads a name along a path, where its spelling and the name it reaches differ: the one owner the agents'
/// policy reads every path segment through on Windows before any guard compares it. A segment's trailing dots and
/// spaces are dropped (<c>.git.</c>, <c>k </c>), as Windows drops them; an NTFS stream or attribute suffix after its
/// first colon is dropped (<c>.git::$INDEX_ALLOCATION</c>, <c>k::$DATA</c>), for it names the file or directory itself or
/// a stream of it; and a short name (<c>GIT~1</c>, <c>harnes~1</c>), which the system gives as its long one where the path
/// exists, may be any name it could shorten where it does not - one a command may make before the word is read.
/// </summary>
public static partial class WindowsNames
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

        var segments = Regex.Split(path, @"([/\\]+)");

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

        return ShortName().IsMatch(segment);
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

        if (ShortName().Match(segment) is not { Success: true } match)
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
    /// <paramref name="names"/> it may shorten (<see cref="MayShorten"/>), in every combination.
    /// </summary>
    /// <param name="path">A whole path, after the system has given each short name it knows as its long one.</param>
    /// <param name="names">The names a short name is read as where it may shorten one.</param>
    /// <param name="most">The most readings followed; past them the path is not decided.</param>
    /// <exception cref="NotSupportedException">The path spells more readings than <paramref name="most"/>.</exception>
    public static IReadOnlyList<string> Lengthened(string path, IReadOnlyCollection<string> names, int most)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(names);

        if (!path.Contains('~', StringComparison.Ordinal))
        {
            return [path];
        }

        var segments = Regex.Split(path, @"([/\\]+)");
        var choices = segments.Select((segment, at) => at % 2 == 0 && IsShortName(segment)
                ? [segment, .. names.Where(name => MayShorten(segment, name)).Distinct(StringComparer.OrdinalIgnoreCase)]
                : new[] { segment })
            .ToList();
        var count = choices.Aggregate(1L, (product, choice) => product * choice.Length);

        if (count == 1)
        {
            return [path];
        }

        if (count > most)
        {
            throw new NotSupportedException($"'{path}' may name more than {most} places by its short names, which this does not follow");
        }

        IEnumerable<IEnumerable<string>> combinations = [[]];

        foreach (var choice in choices)
        {
            combinations = [.. combinations.SelectMany(combination => choice.Select(name => combination.Append(name)))];
        }

        return [.. combinations.Select(combination => string.Concat(combination))];
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

    /// <summary>A short name as Windows spells one: a stem of one to six characters, a <c>~</c> and its number, and an extension of up to three.</summary>
    [GeneratedRegex(@"^(?<stem>[^~.\s/\\:*?""<>|]{1,6})~[0-9]{1,6}(\.(?<extension>[^.\s/\\:*?""<>|]{1,3}))?$")]
    private static partial Regex ShortName();
}
