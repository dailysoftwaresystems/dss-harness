using System.Text;

namespace DssHarness.Core.Codebase.Languages.Scanning;

/// <summary>
/// What the comments of a scanned text say, alike for every scanned language that writes C's comments - <c>//</c>,
/// <c>///</c>, <c>//!</c>, <c>/* */</c> and <c>/** */</c> - and PHP's <c>#</c> too: the first sentence a declaration's
/// comments give of it, and whether a generator wrote the file.
/// </summary>
internal static class ScanComments
{
    /// <summary>The marks a line of C's comments begins with.</summary>
    private static readonly char[] SlashMarks = ['/', '*', '!', '<'];

    /// <summary>The mark a line of a comment a <c>#</c> opens begins with.</summary>
    private static readonly char[] HashMarks = ['#'];

    /// <summary>
    /// The first sentence the comments directly above the token at <paramref name="at"/> say: their prose, each line's
    /// comment marks left out, a shebang none of it, a <c>@brief</c>'s or <c>\brief</c>'s text taken and any other documentation command ending
    /// it; <see langword="null"/> where they say none.
    /// </summary>
    /// <param name="scan">The text.</param>
    /// <param name="at">The index of a declaration's first token.</param>
    public static string? Summary(ScannedText scan, int at)
    {
        var text = new StringBuilder();

        foreach (var comment in scan.CommentsAbove(at))
        {
            // A shebang says how the file is run, never what a declaration is.
            if (comment.Start == 0 && scan.Source.StartsWith("#!"))
            {
                continue;
            }

            // A comment a # opens - PHP's - is marked by its #; C's by its slashes, stars and their marks.
            var marks = scan.Source[comment.Start] == '#' ? HashMarks : SlashMarks;

            foreach (var line in CodeFileText.Lines(scan.Source.Substring(comment.Start, comment.Length)))
            {
                var prose = line.Trim().TrimStart(marks).TrimEnd('/', '*').Trim();

                if (prose.StartsWith("@brief", StringComparison.Ordinal) || prose.StartsWith("\\brief", StringComparison.Ordinal))
                {
                    prose = prose[6..].Trim();
                }
                else if (prose.StartsWith('@') || prose.StartsWith('\\'))
                {
                    if (text.Length > 0)
                    {
                        return Sentence(text);
                    }

                    continue;
                }

                text.Append(prose).Append(' ');
            }
        }

        return Sentence(text);
    }

    /// <summary>
    /// Why the text is generated code: what a comment of its leading run - every comment before its first token of code,
    /// however many lines they run over, as the generators' own conventions place the mark - says, as a generator writes:
    /// <c>DO NOT EDIT</c>, <c>DO NOT MODIFY BY HAND</c> (Dart's builders) or <c>@generated</c>; <see langword="null"/>
    /// where none does.
    /// </summary>
    /// <param name="scan">The text.</param>
    public static string? Generated(ScannedText scan)
    {
        var code = scan.Count > 0 ? scan[0].Start : long.MaxValue;

        foreach (var comment in scan.Comments.TakeWhile(comment => comment.Start < code))
        {
            var (start, end) = (comment.Start, comment.Start + comment.Length);

            if (scan.Source.Find("DO NOT EDIT", start, end) >= 0)
            {
                return "its first lines say DO NOT EDIT, as a generator writes";
            }

            if (scan.Source.Find("DO NOT MODIFY BY HAND", start, end) >= 0)
            {
                return "its first lines say DO NOT MODIFY BY HAND, as a generator writes";
            }

            if (scan.Source.Find("@generated", start, end) >= 0)
            {
                return "its first lines say @generated, as a generator writes";
            }
        }

        return null;
    }

    private static string? Sentence(StringBuilder text) => ExtractText.Flat(text.ToString()) is { Length: > 0 } flat ? ExtractText.Summary(flat) : null;
}
