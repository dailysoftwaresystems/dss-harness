using System.Globalization;
using System.Text.RegularExpressions;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// Markdown's outline: each heading - written with <c>#</c> marks, or underlined with <c>=</c> or <c>-</c> - is a
/// <c>section</c> holding its lines up to the next heading of its level or above, inside the section of the nearest
/// heading above it of a higher level, so one section can be served alone. A heading inside a fenced block is code, and
/// no heading.
/// </summary>
public sealed partial class MarkdownExtractor : ICodeGraphExtractor
{
    /// <inheritdoc/>
    public string Language => "markdown";

    /// <inheritdoc/>
    public int Version => 1;

    /// <inheritdoc/>
    public CodeGraphClaims Claims { get; } = CodeGraphClaims.OfExtensions(".md");

    /// <inheritdoc/>
    public bool Outlines => true;

    /// <inheritdoc/>
    public CodeGraphExtractorHelp Help { get; } = new(
        "Markdown (.md): each heading, marked with # or underlined, as a section holding its lines up to the next heading of its level or above",
        [
            "a heading inside a fenced block, which is code",
            "a heading written in HTML (<h1>), and links between documents",
        ]);

    /// <inheritdoc/>
    public FileExtract Extract(CodeFileInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var lines = input.Lines;
        var headings = Headings(lines);
        var nodes = new List<ExtractNode>();
        var open = new List<(int Level, ExtractNode Node, string Title)>();
        var taken = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var at = 0; at < headings.Count; at++)
        {
            var (start, level, title, signature) = headings[at];

            // A section runs to the line before the next heading of its level or above, or to the file's end.
            var next = headings.Skip(at + 1).FirstOrDefault(later => later.Level <= level);
            var end = next.Start > 0 ? next.Start - 1 : lines.Count;

            while (open.Count > 0 && open[^1].Level >= level)
            {
                open.RemoveAt(open.Count - 1);
            }

            var path = string.Join('/', open.Select(section => section.Title).Append(title));
            var id = $"{Language}:{input.Path}#{path}";

            // Two headings of one path in one file - two "Usage" under one parent - are told apart by their order.
            var seen = taken[id] = taken.GetValueOrDefault(id) + 1;
            id = seen == 1 ? id : $"{id}~{seen.ToString(CultureInfo.InvariantCulture)}";

            var node = new ExtractNode(id, CodeNodeKinds.Section, title, $"{input.Path}#{path}", start, end, Cut(signature, FileExtract.MostSignature))
            {
                SubKind = $"h{level.ToString(CultureInfo.InvariantCulture)}",
                Parent = open.Count > 0 ? open[^1].Node.Id : null,
                BodyStart = Math.Min(end, start + (start < end && IsUnderline(lines[start]) ? 2 : 1)),
                Summary = SummaryOf(lines, start, end),
            };

            nodes.Add(node);
            open.Add((level, node, title));
        }

        return new FileExtract(nodes);
    }

    /// <summary>
    /// Every heading of <paramref name="lines"/>, outside fenced blocks: the line it begins on, from 1, its level, its
    /// title and its header as written.
    /// </summary>
    private static List<(int Start, int Level, string Title, string Signature)> Headings(IReadOnlyList<string> lines)
    {
        var headings = new List<(int, int, string, string)>();
        string? fence = null;
        var start = FrontMatterEnd(lines);
        var underlined = -1;

        for (var at = start; at < lines.Count; at++)
        {
            var line = lines[at];

            if (FenceOf(line) is { } opened)
            {
                var marks = opened.Groups["marks"].Value;

                if (fence is null)
                {
                    fence = marks;
                    continue;
                }

                if (marks[0] == fence[0] && marks.Length >= fence.Length && opened.Groups["info"].Value.Trim().Length == 0)
                {
                    fence = null;
                }

                continue;
            }

            if (fence is not null)
            {
                continue;
            }

            if (Atx().Match(line) is { Success: true } atx)
            {
                var title = Closing().Replace(atx.Groups["title"].Value, string.Empty).Trim();
                headings.Add((at + 1, atx.Groups["marks"].Value.Length, title.Length > 0 ? title : "(untitled)", line.Trim()));
                continue;
            }

            // An underline made a heading of the line above it; a rule right under it is a rule, never a heading of it.
            if (at > start && IsUnderline(line) && IsSetextTitle(lines[at - 1]) && underlined != at - 1)
            {
                headings.Add((at, line.TrimStart()[0] == '=' ? 1 : 2, lines[at - 1].Trim(), lines[at - 1].Trim()));
                underlined = at;
            }
        }

        return headings;
    }

    /// <summary>
    /// Where the document begins after its front matter - a first line <c>---</c> and the lines up to the next <c>---</c>
    /// or <c>...</c>, which are data and no text - as an index of <paramref name="lines"/>: 0 where it has none.
    /// </summary>
    private static int FrontMatterEnd(IReadOnlyList<string> lines)
    {
        if (lines[0].TrimEnd() != "---")
        {
            return 0;
        }

        for (var at = 1; at < lines.Count; at++)
        {
            if (lines[at].TrimEnd() is "---" or "...")
            {
                return at + 1;
            }
        }

        return 0;
    }

    /// <summary>The first sentence of a section's first line of text, within <see cref="FileExtract.MostSummary"/> characters.</summary>
    private static string? SummaryOf(IReadOnlyList<string> lines, int start, int end)
    {
        for (var line = start + 1; line <= end; line++)
        {
            var text = lines[line - 1].Trim();

            if (text.Length == 0 || IsUnderline(text))
            {
                continue;
            }

            // What follows is a heading of its own, or code: the section's text, if any, is under it.
            if (Atx().IsMatch(text) || FenceOf(text) is not null || (line < end && IsUnderline(lines[line]) && IsSetextTitle(lines[line - 1])))
            {
                return null;
            }

            var stop = Sentence().Match(text);
            return Cut(stop.Success ? text[..(stop.Index + 1)] : text, FileExtract.MostSummary);
        }

        return null;
    }

    /// <summary><paramref name="text"/> cut, where it is longer than <paramref name="most"/>, at a word before it with an ellipsis - never inside a surrogate pair.</summary>
    private static string Cut(string text, int most)
    {
        if (text.Length <= most)
        {
            return text;
        }

        var cut = text[..(most - 1)];
        cut = char.IsHighSurrogate(cut[^1]) ? cut[..^1] : cut;
        var space = cut.LastIndexOf(' ');

        return (space > most / 2 ? cut[..space] : cut).TrimEnd() + "…";
    }

    private static bool IsUnderline(string line) => Underline().IsMatch(line);

    /// <summary>
    /// Whether an underline below <paramref name="line"/> makes a heading of it: it is a paragraph's text and no heading
    /// itself - a list item, a quote, a table's row, a rule, a fence or indented code under a rule is no heading.
    /// </summary>
    private static bool IsSetextTitle(string line)
        => line.Trim().Length > 0 && !Atx().IsMatch(line) && FenceOf(line) is null && !line.StartsWith("    ", StringComparison.Ordinal) && !NoParagraph().IsMatch(line) && !Rule().IsMatch(line);

    /// <summary>
    /// <paramref name="line"/> read as a fence's line - its marks and its info string - or <see langword="null"/> where it
    /// is none: a backtick fence's info string holds no backtick, and such a line is text with code spans in it.
    /// </summary>
    private static Match? FenceOf(string line)
        => Fence().Match(line) is { Success: true } fence && !(fence.Groups["marks"].Value[0] == '`' && fence.Groups["info"].Value.Contains('`', StringComparison.Ordinal))
            ? fence
            : null;

    [GeneratedRegex(@"^ {0,3}(?<marks>#{1,6})(?:[ \t]+(?<title>.*))?$", RegexOptions.CultureInvariant)]
    private static partial Regex Atx();

    [GeneratedRegex(@"[ \t]+#+[ \t]*$|^#+[ \t]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Closing();

    [GeneratedRegex(@"^ {0,3}(?:=+|-+)[ \t]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Underline();

    [GeneratedRegex(@"^ {0,3}(?<marks>`{3,}|~{3,})(?<info>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex Fence();

    [GeneratedRegex(@"[.!?](?=\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex Sentence();

    /// <summary>A rule: three or more of one of <c>-</c>, <c>*</c> or <c>_</c>, spaces between them allowed.</summary>
    [GeneratedRegex(@"^ {0,3}([-*_])(?:[ \t]*\1){2,}[ \t]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Rule();

    /// <summary>A line that opens no paragraph: a list item, a quote, a table's row.</summary>
    [GeneratedRegex(@"^ {0,3}(?:[-*+](?:[ \t]|$)|\d{1,9}[.)](?:[ \t]|$)|>|\|)", RegexOptions.CultureInvariant)]
    private static partial Regex NoParagraph();
}
