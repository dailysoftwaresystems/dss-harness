using System.Text;

namespace DssHarness.Core.Codebase.Languages.Scanning;

/// <summary>
/// What the comments of a scanned text say, alike for every scanned language that writes C's comments - <c>//</c>,
/// <c>///</c>, <c>//!</c>, <c>/* */</c> and <c>/** */</c>: the first sentence a declaration's comments give of it, and
/// whether a generator wrote the file.
/// </summary>
internal static class ScanComments
{
    /// <summary>How many of a file's first lines a generator's mark is looked for in.</summary>
    private const int GeneratedLines = 30;

    /// <summary>
    /// The first sentence the comments directly above the token at <paramref name="at"/> say: their prose, each line's
    /// comment marks left out, a <c>@brief</c>'s or <c>\brief</c>'s text taken and any other documentation command ending
    /// it; <see langword="null"/> where they say none.
    /// </summary>
    /// <param name="scan">The text.</param>
    /// <param name="at">The index of a declaration's first token.</param>
    public static string? Summary(ScannedText scan, int at)
    {
        var text = new StringBuilder();

        foreach (var comment in scan.CommentsAbove(at))
        {
            foreach (var line in CodeFileText.Lines(scan.Source.Substring(comment.Start, comment.Length)))
            {
                var prose = line.Trim().TrimStart('/', '*', '!', '<').TrimEnd('/', '*').Trim();

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
    /// Why the text is generated code: what a comment among its first <see cref="GeneratedLines"/> lines says, as a
    /// generator writes - <c>DO NOT EDIT</c>, <c>DO NOT MODIFY BY HAND</c> (Dart's builders) or <c>@generated</c>;
    /// <see langword="null"/> where none does.
    /// </summary>
    /// <param name="scan">The text.</param>
    public static string? Generated(ScannedText scan)
    {
        foreach (var comment in scan.Comments.TakeWhile(comment => comment.Line <= GeneratedLines))
        {
            var text = scan.Source.AsSpan(comment.Start, comment.Length);

            if (text.Contains("DO NOT EDIT", StringComparison.Ordinal))
            {
                return "its first lines say DO NOT EDIT, as a generator writes";
            }

            if (text.Contains("DO NOT MODIFY BY HAND", StringComparison.Ordinal))
            {
                return "its first lines say DO NOT MODIFY BY HAND, as a generator writes";
            }

            if (text.Contains("@generated", StringComparison.Ordinal))
            {
                return "its first lines say @generated, as a generator writes";
            }
        }

        return null;
    }

    private static string? Sentence(StringBuilder text) => ExtractText.Flat(text.ToString()) is { Length: > 0 } flat ? ExtractText.Summary(flat) : null;
}
