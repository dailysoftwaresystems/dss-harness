using System.Globalization;
using System.Text.RegularExpressions;

namespace DssHarness.Core.Codebase;

/// <summary>
/// The changes a conversation is told of the codebase (part 08, "Deltas, never a rewrite of what was sent"): a step
/// that goes on with a stored conversation is given, in its one new user message, the declarations its view changed
/// since that conversation's last pack or delta - moved or changed with the new range and hash, removed - and the
/// nodes of its own pack the conversation was not given, added; never a map or a pack again whole, for a conversation's
/// prefix is never changed. A declaration is known by its handle in its file, so one that moved is told where it stands
/// now and not named removed and added. Made by this one owner from the view, the step's own pack and the text the
/// conversation last held, so the same step on the same tree tells the same changes.
/// </summary>
public static partial class CodebaseConversationChanges
{
    /// <summary>One declaration a pack or a delta line names: its handle, where it stands, and the hash of what stands there.</summary>
    /// <param name="Handle">The declaration's handle, as every line of the graph names it.</param>
    /// <param name="Path">The file it is declared in, relative to the tree with forward separators.</param>
    /// <param name="Start">The line its own lines begin at.</param>
    /// <param name="End">The line its own lines end at.</param>
    /// <param name="Hash">The hash of what stands there; null where the line names none.</param>
    /// <param name="Line">The line as it was written.</param>
    public sealed record Stood(string Handle, string Path, int Start, int End, string? Hash, string Line);

    [GeneratedRegex(@"^(.*)\:(\d+)\-(\d+)(?:\s#([0-9a-fA-F]+))?$")]
    private static partial Regex Where();

    /// <summary>
    /// The changes of <paramref name="view"/> since <paramref name="last"/>, as a step that goes on with the
    /// conversation that held it is told them: the declarations that conversation was told of that moved, changed or
    /// were removed - each with its new range and hash where it stands - and the nodes of <paramref name="pack"/> it
    /// was not told of, added, cut at whole lines under <paramref name="tokens"/>. Never null: where nothing changed
    /// and nothing is new, the first line alone says so.
    /// </summary>
    /// <param name="view">The step's view, as its start's sync left it.</param>
    /// <param name="pack">The step's own context pack, made at its start.</param>
    /// <param name="last">The codebase part the conversation held last - its pack, or the delta before this one; null where it held none.</param>
    /// <param name="tokens">The step's own cap, within which the delta is cut at whole lines.</param>
    public static string Make(CodebaseGraphQueries view, ContextPack pack, string? last, int tokens)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(pack);

        var known = LinesOf(last);
        var own = pack.Lines.Where(line => line.Id is not null).Select(line => StoodOf(line.Text)).OfType<Stood>().ToList();
        var held = known.Select(node => KeyOf(node)).ToHashSet(StringComparer.Ordinal);

        var moved = new List<string>();
        var removed = new List<string>();
        var added = new List<string>();

        foreach (var file in known.GroupBy(node => node.Path, StringComparer.Ordinal))
        {
            // The file's declarations as the view holds them now; null where the view no longer holds the file.
            var now = view.OutlineFile(file.Key);

            foreach (var was in file)
            {
                if (now is { } cards && cards.FirstOrDefault(card => card.Handle == was.Handle) is { } stands)
                {
                    // It stands where it stood, as it stood: told already, told again by nothing.
                    if (stands.Start == was.Start && stands.End == was.End && stands.Hash == was.Hash)
                    {
                        continue;
                    }

                    // Moved or changed: its line as the view holds it now, the new range and hash in it.
                    moved.Add(GraphLines.Of(stands));
                }
                else
                {
                    removed.Add(was.Line);
                }
            }
        }

        foreach (var node in own)
        {
            if (!held.Contains(KeyOf(node)))
            {
                added.Add(node.Line);
            }
        }

        var lines = new List<string>
        {
            $"{known.Count} declaration{(known.Count == 1 ? string.Empty : "s")} the conversation held: {moved.Count} moved or changed, {removed.Count} removed; "
                + $"{added.Count} node{(added.Count == 1 ? string.Empty : "s")} of this step's pack added",
        };
        lines.AddRange(moved);
        lines.AddRange(removed.Select(line => $"removed: {line}"));
        lines.AddRange(added);

        var text = string.Join('\n', lines);
        var leftOut = 0;

        while (EstimatedTokens.Of(text) > tokens && text.Count(ch => ch == '\n') > 0)
        {
            var cut = text.LastIndexOf('\n');
            text = text[..cut];
            leftOut++;
        }

        return leftOut > 0 ? $"{text}\n{leftOut} lines left out" : text;
    }

    /// <summary>The node lines of <paramref name="text"/> - a pack's or a delta's - each where it stands; the first line, the last and a declaration taken out are not node lines.</summary>
    /// <param name="text">The codebase part a conversation held, or null where it held none.</param>
    private static List<Stood> LinesOf(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        return [.. text.Split('\n').Select(StoodOf).OfType<Stood>()];
    }

    /// <summary>One line read as a declaration's place, where it holds the form a node line writes; null where it is none.</summary>
    /// <param name="line">The line, whole.</param>
    private static Stood? StoodOf(string line)
    {
        var told = line.Split(" | ");

        // A node line: its handle, kind and signature, then where it stands - `path:start-end #hash` - then what it
        // says of itself. Fewer than two segments name no place; the second is the place where the line is one.
        if (told.Length < 2 || told[0].Split(' ') is not [var handle, ..] || Where().Match(told[1]) is not { Success: true } place)
        {
            return null;
        }

        return new Stood(
            handle,
            place.Groups[1].Value,
            int.Parse(place.Groups[2].Value, CultureInfo.InvariantCulture),
            int.Parse(place.Groups[3].Value, CultureInfo.InvariantCulture),
            place.Groups[4].Success ? place.Groups[4].Value : null,
            line);
    }

    /// <summary>What names a declaration as a set holds it: its handle in its file.</summary>
    private static string KeyOf(Stood node) => $"{node.Path}:{node.Handle}";
}
